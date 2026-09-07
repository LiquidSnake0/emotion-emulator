namespace Emotion.Signal;

/// <summary>
/// Ce que le morceau raconte en hauteurs, par opposition a ce qu'il frappe.
///
/// Un piano ne declenche aucun detecteur d'attaque : son attaque est douce, sa
/// resonance longue, et son energie tient sur quelques raies etroites au lieu de
/// s'etaler comme une percussion. Un systeme qui ne connait que les attaques ne le voit
/// pas — et c'est exactement ce qu'on constate a l'ecoute d'instamata : le piano joue,
/// et rien a l'ecran ne lui repond.
///
/// D'ou cette seconde dimension. Les percussions donnent le rythme, l'harmonie donne
/// la couleur et la respiration.
/// </summary>
/// <param name="Chroma">
/// Douze classes de hauteur, du do au si, octaves repliees. Chacune vaut 0 a 1.
/// C'est la representation classique de l'harmonie : elle ignore l'octave, donc un
/// meme accord joue grave ou aigu donne le meme profil.
/// </param>
/// <param name="Pitch">
/// Classe dominante, 0 pour do jusqu'a 11 pour si. Nulle quand rien de tonal ne sort.
/// </param>
/// <param name="Strength">
/// A quel point cette dominante se detache, 0 a 1. Faible sur un accord dense ou sur
/// du bruit, forte sur une note tenue.
/// </param>
/// <param name="Change">
/// Distance entre le profil courant et le precedent : une impulsion sur un changement
/// d'accord. C'est le pendant harmonique d'une attaque.
/// </param>
/// <param name="Tonality">
/// 0 pour un contenu bruite, 1 pour un contenu franchement tonal. Tire de la platitude
/// spectrale : une percussion etale son energie, une note la concentre. Sert d'axe
/// <i>net contre noye</i> pour le rendu.
/// </param>
public readonly record struct Harmony(
    float[] Chroma,
    int? Pitch,
    float Strength,
    float Change,
    float Tonality)
{
    public const int Classes = 12;

    /// <summary>Rien de tonal : le silence, ou une nappe de bruit.</summary>
    public static Harmony None => new(new float[Classes], null, 0f, 0f, 0f);

    /// <summary>Nom francais de la classe dominante, pour le diagnostic.</summary>
    public string PitchName => Pitch is { } p
        ? new[] { "do", "do#", "re", "mib", "mi", "fa", "fa#", "sol", "sol#", "la", "sib", "si" }[p]
        : "—";
}
