namespace Emotion.Signal;

/// <summary>
/// Une image du signal, telle qu'elle part vers le renderer. Une soixantaine par seconde.
///
/// Tout est normalise entre 0 et 1 pour que le shader n'ait aucune conversion a faire,
/// et pour que le mock et l'entree ligne produisent des valeurs de meme echelle.
///
/// <b>Tout ici vient du son, rien de la base.</b> Le tempo lui-meme est estime a partir
/// des attaques : pitcher un disque de six pour cent ne desynchronise donc rien, la
/// detection suit. C'est la raison d'etre du couple <see cref="Onset"/> /
/// <see cref="Bpm"/>.
/// </summary>
/// <param name="T">Millisecondes depuis le demarrage de la source.</param>
/// <param name="Rms">Niveau global percu, 0 a 1.</param>
/// <param name="Bands">Energie par bande de frequence, grave a aigu, chacune 0 a 1.</param>
/// <param name="Onset">
/// Vrai sur la seule image qui porte une attaque. Une impulsion, jamais un etat :
/// si elle restait vraie toute la duree du temps, le renderer redeclencherait son
/// effet a chaque image et l'ecran resterait fige au maximum.
/// </param>
/// <param name="Phase">
/// Position dans la mesure de quatre temps, 0 a 1, pour animer plus lentement que
/// l'attaque. Nul tant que le tempo n'est pas verrouille.
/// </param>
/// <param name="Bpm">
/// Tempo <b>estime depuis le son</b>. Nul pendant les premieres secondes, le temps
/// que la detection accroche. Le renderer doit donc savoir tourner sans lui.
/// </param>
public readonly record struct VisualFrame(
    long T,
    float Rms,
    float[] Bands,
    bool Onset,
    float? Phase,
    float? Bpm)
{
    /// <summary>Nombre de bandes emises. Fixe : le shader dimensionne ses uniformes dessus.</summary>
    public const int BandCount = 12;
}
