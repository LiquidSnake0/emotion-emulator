using Emotion.Signal;
using Microsoft.AspNetCore.SignalR;

namespace Emotion.Server;

/// <summary>
/// Le point de rencontre. Deux sortes de clients s'y connectent :
///
/// - le renderer projete, qui ne fait qu'ecouter ;
/// - le bandeau de controle sur le telephone, qui ecoute aussi et commande par HTTP.
///
/// Les images du signal ne passent pas par une methode de hub : elles sont poussees par
/// <see cref="SignalWorker"/>, qui a la cadence. Le hub ne porte que l'etat d'arrivee.
/// </summary>
public sealed class VisualHub : Hub
{
    private readonly DeckState _deck;
    private readonly IAudioSource _source;

    public VisualHub(DeckState deck, IAudioSource source)
    {
        _deck = deck;
        _source = source;
    }

    /// <summary>
    /// Un client vient d'ouvrir la page. Il lui faut l'etat des platines tout de suite,
    /// sinon il attendrait la prochaine transition pour avoir sa palette — et un
    /// renderer qui redemarre en plein set projetterait du gris.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("deck", _deck.Current);

        // D'ou vient le son, pour que le bandeau ne l'affirme pas en dur : distinguer
        // un signal fabrique d'une vraie ecoute est la premiere chose a verifier quand
        // le visuel ne bouge pas.
        await Clients.Caller.SendAsync("source", _source.Name);
        await base.OnConnectedAsync();
    }
}
