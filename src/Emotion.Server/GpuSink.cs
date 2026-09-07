using System.Diagnostics;
using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Ecrit chaque image dans l'anneau partage, pour l'unite de rendu externe.
///
/// Il s'abonne au <see cref="FrameBus"/> comme n'importe quel autre consommateur, avec
/// sa propre file : si l'ecriture ralentissait — un disque qui n'est pas vraiment en
/// memoire, une machine chargee — le visuel web n'en saurait rien.
///
/// Il mesure aussi le temps qu'il met a livrer, parce que c'est la seule facon de savoir
/// si le tuyau tient ses promesses une fois CUDA branche de l'autre cote.
/// </summary>
public sealed class GpuSink : BackgroundService
{
    private readonly FrameBus _bus;
    private readonly DeckState _deck;
    private readonly ILogger<GpuSink> _log;
    private readonly string _path;

    private uint _sequence;
    private long _written;
    private double _totalUs;
    private double _worstUs;

    // Un maximum brut ne dit rien d'utile : un seul message a trois millisecondes,
    // au tout premier passage, le fixe pour toute la soiree. Ce qui compte est combien
    // de fois on derape, et de combien. Un depassement isole au demarrage est le JIT ;
    // des depassements repetes en plein set sont un vrai probleme.
    private long _over100us;
    private long _over1ms;

    public GpuSink(FrameBus bus, DeckState deck, IConfiguration cfg, ILogger<GpuSink> log)
    {
        _bus = bus;
        _deck = deck;
        _log = log;
        _path = cfg["Gpu:Ring"] ?? SharedRing.DefaultPath;
    }

    /// <summary>Etat du tuyau, pour le diagnostic.</summary>
    public (long Written, double MeanUs, double WorstUs, long Over100us, long Over1ms) Stats() =>
        (_written, _written > 0 ? _totalUs / _written : 0, _worstUs, _over100us, _over1ms);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        SharedRingWriter writer;
        try
        {
            writer = new SharedRingWriter(_path);
        }
        catch (Exception e)
        {
            // L'anneau est un supplement, pas une condition : sans lui le visuel web
            // tourne exactement pareil. Un serveur qui refuserait de demarrer parce
            // qu'un tuyau optionnel n'a pas pu s'ouvrir serait plus fragile qu'utile.
            _log.LogWarning("anneau partage indisponible ({Path}) : {Message}", _path, e.Message);
            return;
        }

        using (writer)
        {
            _log.LogInformation("Anneau partage ouvert : {Path}, {Size} octets par message",
                                _path, GpuPacket.Size);

            var sub = _bus.Subscribe("gpu", capacity: 4);

            await foreach (var frame in sub.ReadAllAsync(ct))
            {
                var t0 = Stopwatch.GetTimestamp();

                var packet = GpuPacket.From(frame, _deck.Current.Playing, _sequence++);
                writer.Write(packet);

                var us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                _written++;
                _totalUs += us;
                if (us > _worstUs) _worstUs = us;
                if (us > 100) _over100us++;
                if (us > 1000) _over1ms++;
            }
        }
    }
}
