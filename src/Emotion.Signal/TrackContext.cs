namespace Emotion.Signal;

/// <summary>
/// Le morceau pose, tel que Crate le connait. Envoye une fois par changement de face,
/// pas soixante fois par seconde.
///
/// <b>Aucun tempo ici, et c'est deliberé.</b> Un BPM stocke est faux des que le fader
/// bouge, et le crate se joue justement en pitchant. Le rythme est deduit du son par
/// <see cref="IAudioSource"/> ; la base ne dit que le caractere. Cette separation est
/// la regle de l'architecture : <b>le son donne le mouvement, la base donne la
/// couleur.</b>
/// </summary>
/// <param name="Title">Titre, pour le bandeau de reglage. Jamais projete.</param>
/// <param name="Disc">Disque, pour le bandeau.</param>
/// <param name="Side">Sigle de face : A, B, C, D. C'est par la que Selim reconnait un morceau.</param>
/// <param name="Camelot">Tag Camelot, par exemple 8A. Pilote la geometrie, pas la vitesse.</param>
/// <param name="Family">Famille de couleur du crate : M-, M, M+, B-, B, B+, R, V, S-, S.</param>
/// <param name="ColorHex">Couleur mesuree de la famille, telle qu'elle sort des pastilles physiques.</param>
/// <param name="CoverUrl">Pochette, pointee chez Bandcamp et jamais recopiee.</param>
public sealed record TrackContext(
    string Title,
    string Disc,
    string Side,
    string Camelot,
    string Family,
    string ColorHex,
    string? CoverUrl)
{
    /// <summary>Le phenomene a projeter, deduit de la famille.</summary>
    public Scene Scene => Scene.ForFamily(Family);

    /// <summary>
    /// Rien ne joue encore. Le renderer doit pouvoir demarrer sans morceau : au
    /// lancement, et entre deux disques.
    /// </summary>
    public static readonly TrackContext Silence =
        new("—", "—", "", "", "", "#6E6E6E", null);
}
