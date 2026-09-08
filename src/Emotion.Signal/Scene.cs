namespace Emotion.Signal;

/// <summary>
/// Le phenomene projete pour une famille de couleur. C'est le coeur du projet :
/// « des vagues sur les M-, des tonnerres sur les M+ ».
///
/// <b>Cette table est une proposition, pas une regle du domaine.</b> Elle vient de la
/// forme de la palette — deux voies paralleles, les bleus et les verts, chacune du
/// clair au fonce, plus quatre familles a part — et pas d'une intention que le DJ
/// aurait ecrite. Elle est faite pour etre corrigee famille par famille, a l'ecoute.
///
/// La lecture proposee : le suffixe donne l'intensite, la lettre donne l'element.
/// M est l'eau et le ciel, de la vague a l'orage. B est le vegetal, de la brise a la
/// futaie. Le reste sont des caracteres isoles.
/// </summary>
public enum Kind
{
    /// <summary>Aucun morceau pose : une respiration lente, rien qui accroche l'oeil.</summary>
    Rest,

    // Voie des bleus : l'eau, puis le ciel.
    Waves,      // M-   ressac large et lent
    Swell,      // M    houle, plus de masse
    Thunder,    // M+   orage, l'onde de choc devient eclair

    // Voie des verts : le vegetal.
    Breeze,     // B-   frondaisons qui bougent a peine
    Grove,      // B    masse feuillue, mouvement dense
    Roots,      // B+   sous-bois sombre, verticales lentes

    // Les caracteres isoles.
    Bloom,      // R    floraison, expansion douce depuis le centre
    Nebula,     // V    nuee, diffusion sans contour
    Ember,      // S-   braises, points chauds qui palpitent
    Void,       // S    le noir, la figure se dessine en creux
}

/// <summary>
/// Ce que le renderer recoit : quoi dessiner, et a quelle force.
/// </summary>
/// <param name="Kind">Le phenomene.</param>
/// <param name="Intensity">
/// 0 a 1, tiree du suffixe de la famille. Un M- et un M+ partagent l'element mais pas
/// la violence : c'est ce qui fait qu'une montee de set se voit a l'ecran.
/// </param>
public readonly record struct Scene(Kind Kind, float Intensity)
{
    public static readonly Scene Rest = new(Kind.Rest, 0f);

    /// <summary>
    /// Traduit une famille du crate en phenomene. Une famille inconnue ne fait pas
    /// echouer le set : elle retombe sur <see cref="Rest"/>, un fond calme, plutot que
    /// sur un ecran noir au milieu d'un morceau.
    /// </summary>
    public static Scene ForFamily(string? family) => (family ?? "").Trim().ToUpperInvariant() switch
    {
        "M-" => new(Kind.Waves,   0.35f),
        "M"  => new(Kind.Swell,   0.65f),
        "M+" => new(Kind.Thunder, 1.00f),

        "B-" => new(Kind.Breeze,  0.35f),
        "B"  => new(Kind.Grove,   0.65f),
        "B+" => new(Kind.Roots,   1.00f),

        "R"  => new(Kind.Bloom,   0.60f),
        "V"  => new(Kind.Nebula,  0.60f),
        "S-" => new(Kind.Ember,   0.50f),
        "S"  => new(Kind.Void,    0.80f),

        _ => Rest,
    };
}
