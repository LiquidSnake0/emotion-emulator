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
    private readonly string cfgPathRetour;

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
        cfgPathRetour = cfg["Gpu:Feedback"] ?? "/dev/shm/emotion-feedback";
    }

    /// <summary>Etat du tuyau, pour le diagnostic.</summary>
    public (long Written, double MeanUs, double WorstUs, long Over100us, long Over1ms) Stats() =>
        (_written, _written > 0 ? _totalUs / _written : 0, _worstUs, _over100us, _over1ms);

    protected override Task ExecuteAsync(CancellationToken ct)
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
            return Task.CompletedTask;
        }

        _writer = writer;
        _log.LogInformation("Anneau partage ouvert : {Path}, {Size} octets par message, ecriture en ligne",
                            _path, GpuPacket.Size);

        // En ligne, et non par une file. L'ecriture ne bloque pas, n'alloue pas et ne
        // peut pas echouer : elle peut donc se faire dans le fil qui vient d'analyser,
        // ce qui supprime un reveil de tache. Ce reveil coutait plus cher que l'ecriture
        // elle-meme, qui se compte en microsecondes.
        // Le canal de retour est encore plus facultatif que l'anneau : personne ne publie
        // dessus tant qu'aucune unite de rendu externe n'est branchee. On l'ouvre quand
        // meme, pour que le jour ou elle arrive, rien n'ait a etre redemarre.
        try
        {
            _retour = new FeedbackChannel(cfgPathRetour);
            _log.LogInformation("Canal de retour ouvert : {Path}", _retour.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("canal de retour indisponible : {Message}", e.Message);
        }

        _bus.AddInlineSink(OnFrame);

        ct.Register(() =>
        {
            _writer?.Dispose(); _writer = null;
            _retour?.Dispose(); _retour = null;
        });
        return Task.CompletedTask;
    }

    private SharedRingWriter? _writer;
    private FeedbackChannel? _retour;

    /// <summary>
    /// Ce que l'unite de rendu a renvoye en dernier, et depuis combien de temps.
    ///
    /// C'EST LE SEUL CHIFFRE QUI MANQUAIT AU BUDGET DE LATENCE.
    ///
    /// Les 48 ms qui separent le son du paquet sont mesurees. Ce qui vient apres — lecture
    /// du paquet, rendu, affichage — etait jusqu'ici <b>estime</b>, et une estimation ne se
    /// regle pas. Des qu'une unite de rendu publie son retour, la chaine entiere devient
    /// mesurable de bout en bout, et l'avance peut se calculer au lieu de se deviner.
    /// </summary>
    public RenderFeedback? DernierRetour { get; private set; }
    public long RetourDepuisMs { get; private set; }

    private void OnFrame(VisualFrame frame)
    {
        var w = _writer;
        if (w is null) return;

        var t0 = Stopwatch.GetTimestamp();

        var packet = GpuPacket.From(frame, _deck.Current.Playing, _sequence++);
        w.Write(packet);

        var us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _written++;
        _totalUs += us;
        if (us > _worstUs) _worstUs = us;
        if (us > 100) _over100us++;
        if (us > 1000) _over1ms++;

        // On relit le retour a chaque image : c'est une lecture d'une ligne de cache, sans
        // verrou et sans attente, et elle ne vaut que si elle est fraiche.
        if (_retour is not null && _retour.TryRead(out var r))
        {
            DernierRetour = r;
            RetourDepuisMs = frame.T - r.PacketTimeMs;
        }
    }
}
