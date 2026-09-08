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
                                        IHubContext<VisualHub> hub, IAudioSource source,
                                        TrackMemory memory) =>
        {
            var next = deck.Apply(d => d.Cue(track));

            // Une face arrive au casque. Si c'est la meme qu'avant — l'aiguille repasse
            // pour verifier le tempo — rien n'est remis a zero : ce passage vient s'ajouter
            // aux precedents, et c'est la que le systeme apprend le plus.
            if (Cue(source) is { } casque) memory.Cue(track, casque);
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // La transition est faite : ce qui etait cale devient ce qui joue. C'est le
        // seul geste qui change la projection.
        app.MapPost("/deck/take", async (DeckState deck, IHubContext<VisualHub> hub,
                                         IAudioSource source, TrackMemory memory) =>
        {
            var next = deck.Apply(d => d.Take());

            // L'ANALYSE DOIT APPRENDRE LE CHANGEMENT DE LA BASE, PAS DU SIGNAL.
            //
            // C'est la seule chose que la fiche sait et que le son ne dira pas a temps :
            // les estimateurs oublient lentement par construction, et mettraient des
            // dizaines de secondes a admettre qu'un autre disque joue. Pendant tout ce
            // temps, le systeme annoncerait qu'il « connait » un morceau qui ne passe
            // plus.
            source.NewTrack();

            // CE QU'ON AVAIT APPRIS NE SE JETTE PAS AVEC LE DISQUE.
            //
            // NewTrack efface les estimateurs, et il le faut : ils decrivent le son qui
            // vient de s'arreter. Mais les portraits des sources et le tempo etabli
            // decrivent le disque, pas l'instant — ils sont ranges, et ceux du disque qui
            // arrive sont repris. Un disque deja passe dans la soiree recommence donc la
            // ou il s'etait arrete au lieu de tout redecouvrir.
            // CE QUE LE CASQUE A APPRIS SUIT LE DISQUE JUSQU'AUX ENCEINTES.
            //
            // NewTrack efface les estimateurs, et il le faut : ils decrivent le son qui
            // vient de s'arreter. Mais les portraits des sources et le tempo etabli
            // decrivent la face, pas l'instant — et cette face vient de passer une minute
            // au casque. Les recalculer serait payer deux fois, au seul moment ou l'on n'a
            // pas le temps.
            //
            // C'est le seul instant du systeme ou quoi que ce soit est recopie. Une
            // transition est un geste ; rien de ceci ne tourne pendant l'analyse.
            if (Master(source) is { } platine) memory.Handover(next.Playing, platine, Cue(source));

            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // Renoncement : la face calee est abandonnee.
        app.MapPost("/deck/drop", async (DeckState deck, IHubContext<VisualHub> hub,
                                         IAudioSource source, TrackMemory memory) =>
        {
            var next = deck.Apply(d => d.Drop());

            // Le vinyle est range : ce qu'on savait de lui part avec. Le garder ne servirait
            // qu'a occuper de la place pour une face qui ne reviendra pas ce soir.
            memory.Forget(cue: true, Cue(source));
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // Pose directement ce qui joue, sans passer par le casque. Sert au demarrage
        // d'un set et aux essais.
        app.MapPost("/deck/play", async (TrackContext track, DeckState deck,
                                         IHubContext<VisualHub> hub, IAudioSource source,
                                         TrackMemory memory) =>
        {
            var next = deck.Apply(_ => new Deck(track, null));
            source.NewTrack();
            if (Master(source) is { } platine) memory.Play(track, platine);
            await hub.Clients.All.SendAsync("deck", next);
            return Results.Ok(next);
        });

        // LE CALAGE SE FAIT DEPUIS LA PISTE, PAS DEPUIS LA TABLE.
        //
        // L'avance a donner au visuel depend d'ou l'on ecoute : le son met 5,8 ms pour
        // atteindre celui qui regle a la table, et 29 pour le public a dix metres. Regler
        // depuis la table revient donc a faire preceder le mur de vingt-trois
        // millisecondes pour tout le monde d'autre — un ecart plus grand que tout ce que
        // l'analyse a gagne en une soiree de mesures.
        //
        // Le reglage passe donc par le telephone : on se place ou sera le public, on
        // regarde le mur, et l'on corrige jusqu'a ce que la forme tombe avec la frappe.
        // C'est le seul endroit d'ou le jugement soit juste.
        app.MapPost("/lead", async (LeadRequest r, IHubContext<VisualHub> hub) =>
        {
            var ms = Math.Clamp(r.Ms, 0, 200);
            await hub.Clients.All.SendAsync("lead", ms);
            return Results.Ok(new { ms });
        });

        app.MapGet("/deck", (DeckState deck) => Results.Ok(deck.Current));
    }

    /// <summary>L'avance demandee au visuel, en millisecondes.</summary>
    public sealed record LeadRequest(float Ms);

    /// <summary>La face qui joue, si elle sait apprendre.</summary>
    private static ILearnsTracks? Master(IAudioSource source) => source switch
    {
        DualAudioSource d => d.Master as ILearnsTracks,
        ILearnsTracks l => l,
        _ => null,
    };

    /// <summary>La face calee au casque, si elle sait apprendre.</summary>
    private static ILearnsTracks? Cue(IAudioSource source) =>
        source is DualAudioSource d ? d.Cue as ILearnsTracks : null;
}
