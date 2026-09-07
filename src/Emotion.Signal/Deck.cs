namespace Emotion.Signal;

/// <summary>
/// L'etat des platines : ce qui joue, et ce qui se prepare.
///
/// <b>Le morceau prepare ne doit jamais atteindre le mur.</b> Selim cale son prochain
/// disque au casque ; si la projection changeait au moment ou il le selectionne, le
/// public verrait le beatmatch commencer, c'est-a-dire la coulisse. Le visuel ne bascule
/// donc qu'au signal explicite de <c>POST /deck/take</c>, quand la transition est faite.
///
/// Le prepare existe quand meme dans le modele : il alimente le bandeau de controle,
/// affiche sur le telephone et jamais projete, et il laissera plus tard au renderer le
/// temps de precharger la pochette et les clips de la face qui vient.
/// </summary>
/// <param name="Playing">Ce qui sort des enceintes et pilote la projection.</param>
/// <param name="Cued">
/// Ce qui est cale au casque, ou nul. Visible du seul DJ.
/// </param>
public sealed record Deck(TrackContext Playing, TrackContext? Cued)
{
    public static readonly Deck Empty = new(TrackContext.Silence, null);

    /// <summary>Selim pose une face au casque.</summary>
    public Deck Cue(TrackContext next) => this with { Cued = next };

    /// <summary>
    /// La transition est faite : le prepare devient le joue, et la place se libere.
    /// Appeler sans rien de prepare ne fait rien, plutot que de couper la projection
    /// en plein set.
    /// </summary>
    public Deck Take() => Cued is null ? this : new Deck(Cued, null);

    /// <summary>Selim renonce a la face calee.</summary>
    public Deck Drop() => this with { Cued = null };
}
