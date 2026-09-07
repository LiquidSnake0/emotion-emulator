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
    private readonly ILogger<SignalWorker> _log;

    public SignalWorker(IAudioSource source, IHubContext<VisualHub> hub, ILogger<SignalWorker> log)
    {
        _source = source;
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Source du signal : {Source}", _source.Name);

        await foreach (var frame in _source.ReadAsync(ct))
        {
            // SendAsync et non un flux SignalR : une image perdue n'a aucune valeur,
            // la suivante arrive dans seize millisecondes. Mieux vaut sauter que
            // prendre du retard sur le son.
            await _hub.Clients.All.SendAsync("frame", frame, ct);
        }
    }
}
