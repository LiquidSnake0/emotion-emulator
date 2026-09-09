using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Tire les images de la source et les pousse aux renderers, sans interruption tant
/// que le service tourne.
///
/// Il diffuse meme sans client connecte : la source garde ainsi une horloge continue,
/// et un renderer qui se branche en cours de set tombe sur la bonne phase au lieu de
/// repartir de zero.
/// </summary>
public sealed class SignalWorker : BackgroundService
{
    private readonly IAudioSource _source;

    /// <summary>
    /// LE SEUL DEBOUCHE DE LA BOUCLE.
    ///
    /// Elle a longtemps eu deux sorties : le bus, qui alimente l'unite de rendu, et une
    /// diffusion SignalR vers le navigateur. Le navigateur n'existe plus — ce qui affiche
    /// lit les 256 octets de l'anneau partage, aujourd'hui une fenetre Qt, demain un eGPU
    /// sur PCIe ou USB-C. Il ne reste donc qu'une sortie, et elle ne passe par aucun
    /// reseau.
    /// </summary>
    private readonly FrameBus _bus;
    private readonly ILogger<SignalWorker> _log;

    public SignalWorker(IAudioSource source, FrameBus bus, ILogger<SignalWorker> log)
    {
        _source = source;
        _bus = bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Source du signal : {Source}", _source.Name);

        var announced = false;

        // OU PASSENT LES TROIS SECONDES ET DEMIE ?
        //
        // Mesure a la reception, avec un compteur passif dans la page : sur quarante-six
        // secondes, les images arrivent regulierement — mediane 18,6 ms pour un pas nominal
        // de 21,3 — sauf UN TROU DE 3 390 ms. Et l'horodatage serveur saute de 3 412 ms au
        // meme endroit : ce n'est donc pas le reseau, c'est la boucle qui s'est arretee.
        //
        // Deux phases peuvent l'expliquer et il faut les separer, sinon on optimise au
        // hasard : l'attente de l'image suivante (capture et analyse) et la publication sur
        // le bus (anneau partage, cense etre en microsecondes). Une troisieme a disparu
        // avec le navigateur — la diffusion SignalR, qui etait `await`ee alors que le
        // commentaire d'a cote promettait qu'on saute plutot que d'attendre.
        //
        // Le ramasse-miettes se refute ou se confirme d'une ligne : GetTotalPauseDuration
        // rend le temps total pendant lequel il a arrete le monde. S'il n'a pas bouge
        // pendant le trou, il est hors de cause.
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var pauseAvant = GC.GetTotalPauseDuration();
        var gen2Avant = GC.CollectionCount(2);
        double attente = 0, bus = 0;
        var debutAttente = chrono.Elapsed.TotalMilliseconds;

        await foreach (var frame in _source.ReadAsync(ct))
        {
            attente = chrono.Elapsed.TotalMilliseconds - debutAttente;

            // LE FEU VERT, UNE FOIS ET UNE SEULE.
            //
            // C'est la reponse a la seule question qu'aucun autre maillon ne peut
            // trancher : le systeme en sait-il assez sur ce disque pour qu'on bascule
            // dessus. Le telephone la lit sur /ready, il ne la recoit plus poussee.
            //
            // Une fois : republier a chaque image ferait vibrer une notification
            // cinquante fois par seconde pour un evenement qui n'arrive qu'une seule.
            if (!announced && frame.Readiness.Ready)
            {
                announced = true;
                _log.LogInformation(
                    "Pret sur ce disque par {Reason}", frame.Readiness.Reason);
            }

            // Le bus, et rien d'autre : c'est lui qui alimente l'unite de rendu, dont la
            // latence est desormais la seule qui compte.
            var t0 = chrono.Elapsed.TotalMilliseconds;
            _bus.Publish(frame);
            bus = chrono.Elapsed.TotalMilliseconds - t0;

            // Un tour de boucle qui depasse quatre pas d'analyse est un trou, pas une
            // hesitation. On le nomme, avec la part de chaque phase et ce que le
            // ramasse-miettes a coute pendant ce temps.
            var tour = attente + bus;
            if (tour > 85)
            {
                var pause = (GC.GetTotalPauseDuration() - pauseAvant).TotalMilliseconds;
                var gen2 = GC.CollectionCount(2) - gen2Avant;
                _log.LogWarning(
                    "Trou de {Tour:F0} ms — attente {Attente:F0} · bus {Bus:F0} " +
                    "· pause GC {Pause:F0} ms · {Gen2} collecte(s) gen2",
                    tour, attente, bus, pause, gen2);
            }

            pauseAvant = GC.GetTotalPauseDuration();
            gen2Avant = GC.CollectionCount(2);
            debutAttente = chrono.Elapsed.TotalMilliseconds;
        }
    }
}
