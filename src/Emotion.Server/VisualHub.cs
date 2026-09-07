using Emotion.Signal;
using Microsoft.AspNetCore.SignalR;

namespace Emotion.Server;

/// <summary>
/// Le point de rencontre. Deux sortes de clients s'y connectent :
///
/// - le renderer projete, qui ne fait qu'ecouter ;
/// - Crate, qui annonce le morceau en cours quand Selim pose une face.
///
/// Les images du signal ne passent pas par une methode du hub : elles sont poussees
/// par <see cref="SignalWorker"/>, qui a la cadence. Le hub ne sert qu'aux evenements
/// rares et a l'etat d'arrivee.
/// </summary>
public sealed class VisualHub : Hub
{
    private readonly TrackState _state;

    public VisualHub(TrackState state) => _state = state;

    /// <summary>
    /// Un renderer vient d'ouvrir la page. Il a besoin du morceau en cours tout de
    /// suite, sinon il attendrait le prochain changement de face pour avoir sa palette.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("track", _state.Current);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Crate annonce une nouvelle face. On garde la valeur pour les renderers qui se
    /// connecteront apres, puis on diffuse a ceux qui sont deja la.
    /// </summary>
    public async Task SetTrack(TrackContext track)
    {
        _state.Current = track;
        await Clients.All.SendAsync("track", track);
    }
}
