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
/// <param name="Hits">
/// Qui a frappe, par registre. C'est ce qui permet d'attribuer un effet visuel a un
/// instrument plutot qu'a « du son » : l'eclair au clap, l'onde de choc au kick.
/// </param>
/// <param name="Harmony">
/// Ce qui sonne : profil de hauteurs, note dominante, changement d'accord, caractere
/// tonal. Les attaques donnent le rythme, l'harmonie donne la couleur — sans elle, un
/// piano joue sans que rien ne lui reponde a l'ecran.
/// </param>
/// <param name="Voices">
/// Ce qui joue des notes, reparti en trois registres. Les attaques couvrent ce qui
/// frappe ; un piano, un xylophone ou une voix ne frappent pas, et un chromagramme
/// global les melange en un seul profil — on sait alors quelle note sonne, jamais qui
/// la joue.
/// </param>
/// <param name="Timbre">
/// La couleur du son plutot que ses evenements : brillance, ouverture du filtre,
/// densite. Un passe-bas qu'on ferme sur huit mesures ne change ni le tempo, ni les
/// attaques, ni les notes — sans cette mesure, le systeme reste impassible pendant le
/// geste le plus visible d'un set.
/// </param>
/// <param name="Novelty">
/// Ecart a la texture des dernieres secondes, 0 a 1. Capte ce qu'aucun autre detecteur
/// ne voit : une voix, un sample, une nappe qui entre. Les autres cherchent chacun une
/// chose precise et ne voient donc que ce qu'on leur a appris a voir.
/// </param>
/// <param name="NoveltyOnset">
/// Vrai sur la seule fenetre ou la nouveaute franchit son seuil. Une impulsion, sinon
/// l'effet resterait allume tout le temps que dure la voix.
/// </param>
/// <param name="Blend">
/// Part du morceau prepare deja passee dans le master, 0 a 1, <b>mesuree et non
/// declaree</b>. C'est elle qui fait glisser la projection d'un phenomene a l'autre au
/// rythme du fader, au lieu de basculer sur un bouton.
/// </param>
/// <param name="Flux">
/// Montee du spectre depuis la fenetre precedente, normalisee. Diagnostic : c'est la
/// grandeur qui decide des attaques, et on ne peut pas regler ce qu'on ne voit pas.
/// </param>
/// <param name="Threshold">
/// Seuil courant, normalise sur la meme echelle que <paramref name="Flux"/>. Une
/// attaque tombe quand le flux le depasse.
/// </param>
public readonly record struct VisualFrame(
    long T,
    float Rms,
    float[] Bands,
    bool Onset,
    float? Phase,
    float? Bpm,
    Hits Hits = default,
    Harmony Harmony = default,
    Voices Voices = default,
    Timbre Timbre = default,
    Structure Structure = default,
    float Novelty = 0f,
    bool NoveltyOnset = false,
    float Blend = 0f,
    float Flux = 0f,
    float Threshold = 0f)
{
    /// <summary>Nombre de bandes emises. Fixe : le shader dimensionne ses uniformes dessus.</summary>
    public const int BandCount = 12;
}
