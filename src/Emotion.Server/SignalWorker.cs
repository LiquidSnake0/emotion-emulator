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
    /// Ou pousser les images, s'il y a quelqu'un. La boucle ne connait plus SignalR : elle
    /// peut tourner sans hote web et sans port ouvert. Voir <see cref="IDiffusion"/>.
    /// </summary>
    private readonly IDiffusion _diffusion;
    private readonly FrameBus _bus;
    private readonly ILogger<SignalWorker> _log;

    public SignalWorker(IAudioSource source, IDiffusion diffusion,
                        FrameBus bus, ILogger<SignalWorker> log)
    {
        _source = source;
        _diffusion = diffusion;
        _bus = bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Source du signal : {Source}", _source.Name);

        var dual = _source as DualAudioSource;
        var tick = 0;
        var announced = false;

        // OU PASSENT LES TROIS SECONDES ET DEMIE ?
        //
        // Mesure a la reception, avec un compteur passif dans la page : sur quarante-six
        // secondes, les images arrivent regulierement — mediane 18,6 ms pour un pas nominal
        // de 21,3 — sauf UN TROU DE 3 390 ms. Et l'horodatage serveur saute de 3 412 ms au
        // meme endroit : ce n'est donc pas le reseau, c'est la boucle qui s'est arretee.
        //
        // Trois phases peuvent l'expliquer et il faut les separer, sinon on optimise au
        // hasard : l'attente de l'image suivante (capture et analyse), la publication sur
        // le bus (anneau partage, cense etre en microsecondes), et la diffusion SignalR
        // — celle-ci est `await`ee alors que le commentaire d'a cote promet qu'on saute
        // plutot que d'attendre.
        //
        // Le ramasse-miettes se refute ou se confirme d'une ligne : GetTotalPauseDuration
        // rend le temps total pendant lequel il a arrete le monde. S'il n'a pas bouge
        // pendant le trou, il est hors de cause.
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var pauseAvant = GC.GetTotalPauseDuration();
        var gen2Avant = GC.CollectionCount(2);
        double attente = 0, bus = 0, envoi = 0;
        var debutAttente = chrono.Elapsed.TotalMilliseconds;

        await foreach (var frame in _source.ReadAsync(ct))
        {
            attente = chrono.Elapsed.TotalMilliseconds - debutAttente;
            // L'etat du cue part cinq fois par seconde et non cinquante : il alimente
            // un bandeau de preparation, pas une animation. Le debit du master reste
            // entier.
            if (dual is not null && ++tick % 10 == 0)
                await _diffusion.Casque(dual.CueFrame, ct);

            // LE FEU VERT, UNE FOIS ET UNE SEULE.
            //
            // C'est la reponse a la seule question qu'aucun autre maillon ne peut
            // trancher : le systeme en sait-il assez sur ce disque pour qu'on bascule
            // dessus. Elle part vers le telephone, ou le DJ decide.
            //
            // Une fois : republier a chaque image ferait vibrer une notification
            // cinquante fois par seconde pour un evenement qui n'arrive qu'une seule.
            if (!announced && frame.Readiness.Ready)
            {
                announced = true;
                _log.LogInformation(
                    "Pret sur ce disque par {Reason}", frame.Readiness.Reason);
                await _diffusion.Pret(frame.Readiness, ct);
            }

            // Le bus d'abord, et sans attendre : c'est lui qui alimente l'unite de
            // rendu externe, dont la latence compte plus que celle du navigateur.
            var t0 = chrono.Elapsed.TotalMilliseconds;
            _bus.Publish(frame);
            bus = chrono.Elapsed.TotalMilliseconds - t0;

            // SendAsync et non un flux SignalR : une image perdue n'a aucune valeur,
            // la suivante arrive dans vingt-et-une millisecondes. Mieux vaut sauter que
            // prendre du retard sur le son.
            var t1 = chrono.Elapsed.TotalMilliseconds;
            await _diffusion.Image(frame, ct);
            envoi = chrono.Elapsed.TotalMilliseconds - t1;

            // Un tour de boucle qui depasse quatre pas d'analyse est un trou, pas une
            // hesitation. On le nomme, avec la part de chaque phase et ce que le
            // ramasse-miettes a coute pendant ce temps.
            var tour = attente + bus + envoi;
            if (tour > 85)
            {
                var pause = (GC.GetTotalPauseDuration() - pauseAvant).TotalMilliseconds;
                var gen2 = GC.CollectionCount(2) - gen2Avant;
                _log.LogWarning(
                    "Trou de {Tour:F0} ms — attente {Attente:F0} · bus {Bus:F0} · envoi {Envoi:F0} " +
                    "· pause GC {Pause:F0} ms · {Gen2} collecte(s) gen2",
                    tour, attente, bus, envoi, pause, gen2);
            }

            pauseAvant = GC.GetTotalPauseDuration();
            gen2Avant = GC.CollectionCount(2);
            debutAttente = chrono.Elapsed.TotalMilliseconds;
        }
    }
}
