using Emotion.Signal;
using Microsoft.AspNetCore.SignalR;

namespace Emotion.Server;

/// <summary>
/// Les commandes venues du telephone. Quatre verbes, un par geste reel aux platines.
///
/// Le choix de fond : <b>les commandes passent par HTTP, les evenements par SignalR</b>.
/// Ce ne sont pas deux fois le meme canal par negligence, ce sont deux besoins opposes.
/// Une commande est rare, doit etre acquittee et peut echouer ; un evenement est
/// continu, sans reponse, et sa perte est sans consequence puisque le suivant arrive
/// dans seize millisecondes. Les separer permet aussi a Crate de commander avec un
/// simple <c>fetch</c>, sans embarquer un client temps reel dans la PWA.
/// </summary>
public static class DeckEndpoints
{
    public static void MapDeck(this WebApplication app)
    {
        // Cale une face au casque. N'a aucun effet sur la projection : le public ne
        // doit pas voir le beatmatch commencer.
        app.MapPost("/deck/cue", async (TrackContext track, DeckState deck,
                                        IHubContext<VisualHub> hub) =>
        {
            var next = deck.Apply(d => d.Cue(track));
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // La transition est faite : ce qui etait cale devient ce qui joue. C'est le
        // seul geste qui change la projection.
        app.MapPost("/deck/take", async (DeckState deck, IHubContext<VisualHub> hub) =>
        {
            var next = deck.Apply(d => d.Take());
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // Renoncement : la face calee est abandonnee.
        app.MapPost("/deck/drop", async (DeckState deck, IHubContext<VisualHub> hub) =>
        {
            var next = deck.Apply(d => d.Drop());
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // Pose directement ce qui joue, sans passer par le casque. Sert au demarrage
        // d'un set et aux essais.
        app.MapPost("/deck/play", async (TrackContext track, DeckState deck,
                                         IHubContext<VisualHub> hub) =>
        {
            var next = deck.Apply(_ => new Deck(track, null));
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        app.MapGet("/deck", (DeckState deck) => Results.Ok(deck.Current));
    }
}
