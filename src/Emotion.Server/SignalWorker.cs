using Emotion.Signal;
using Microsoft.AspNetCore.SignalR;

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
    private readonly IHubContext<VisualHub> _hub;
    private readonly FrameBus _bus;
    private readonly ILogger<SignalWorker> _log;

    public SignalWorker(IAudioSource source, IHubContext<VisualHub> hub,
                        FrameBus bus, ILogger<SignalWorker> log)
    {
        _source = source;
        _hub = hub;
        _bus = bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Source du signal : {Source}", _source.Name);

        var dual = _source as DualAudioSource;
        var tick = 0;
        var announced = false;

        await foreach (var frame in _source.ReadAsync(ct))
        {
            // L'etat du cue part cinq fois par seconde et non cinquante : il alimente
            // un bandeau de preparation, pas une animation. Le debit du master reste
            // entier.
            if (dual is not null && ++tick % 10 == 0)
                await _hub.Clients.All.SendAsync("cue", dual.CueFrame, ct);

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
                await _hub.Clients.All.SendAsync("ready", frame.Readiness, ct);
            }

            // Le bus d'abord, et sans attendre : c'est lui qui alimente l'unite de
            // rendu externe, dont la latence compte plus que celle du navigateur.
            _bus.Publish(frame);

            // SendAsync et non un flux SignalR : une image perdue n'a aucune valeur,
            // la suivante arrive dans vingt-et-une millisecondes. Mieux vaut sauter que
            // prendre du retard sur le son.
            await _hub.Clients.All.SendAsync("frame", frame, ct);
        }
    }
}
