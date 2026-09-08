using System.Runtime.InteropServices;

namespace Emotion.Signal;

/// <summary>
/// Ce que l'unite de rendu renvoie a l'analyse.
///
/// POURQUOI UN CANAL DE RETOUR, ALORS QUE LE FLUX EST A SENS UNIQUE.
///
/// Tout le budget de latence du projet est chiffre sauf sa derniere moitie. On sait ce que
/// coutent la capture, la fenetre, l'anticipation du sommet et le calcul — 48 ms mesurees.
/// On ne sait rien de ce qui vient apres : le temps que l'unite de rendu met a lire le
/// paquet, a dessiner, et l'ecran a afficher. Ces valeurs sont aujourd'hui <b>estimees</b>,
/// et une estimation ne se regle pas.
///
/// Le retour ne pilote donc rien : il <b>mesure</b>. L'analyse s'en sert pour dire le retard
/// reel de bout en bout au lieu de l'additionner de tete, et pour proposer l'avance juste
/// plutot qu'un chiffre calcule sur des hypotheses.
///
/// UN SEUL EMPLACEMENT, PAS UN ANNEAU.
///
/// Le flux aller est un anneau parce qu'aucune image ne doit se perdre en silence. Ici c'est
/// l'inverse : seul le dernier etat compte, et une mesure vieille de trois images n'a aucune
/// valeur. Un emplacement unique protege par un compteur de version suffit — le lecteur
/// relit tant que le compteur a bouge pendant sa lecture, et ne bloque jamais l'ecrivain.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct RenderFeedback
{
    public const int Size = 64;                    // une ligne de cache exactement
    public const uint MagicValue = 0x454D5552;     // « EMUR »

    [FieldOffset(0)] public uint Magic;

    /// <summary>Sequence du paquet que le rendu vient de traiter. Relie les deux sens.</summary>
    [FieldOffset(4)] public uint Sequence;

    /// <summary>Horodatage du paquet, tel qu'il a ete emis. Recopie tel quel.</summary>
    [FieldOffset(8)] public long PacketTimeMs;

    /// <summary>
    /// Millisecondes ecoulees entre l'emission du paquet et la fin de son rendu, mesurees
    /// par l'unite de rendu sur sa propre horloge.
    ///
    /// C'est le seul chiffre qui manquait au budget : t1 plus t2, mesures au lieu d'etre
    /// supposes. L'ecran vient encore apres, et lui ne se mesure pas de l'interieur.
    /// </summary>
    [FieldOffset(16)] public float RenderMs;

    /// <summary>Images par seconde soutenues par le rendu.</summary>
    [FieldOffset(20)] public float Fps;

    /// <summary>
    /// Combien d'images le rendu a saute depuis le dernier retour.
    ///
    /// Un rendu qui saute n'est pas en panne : le flux part a 47 Hz et rien n'oblige a tout
    /// afficher. Mais il saute d'autant plus qu'il peine, et c'est ce qu'on veut savoir
    /// avant que cela se voie sur le mur.
    /// </summary>
    [FieldOffset(24)] public uint Dropped;

    /// <summary>Ce que le rendu dit de sa propre sante, 0 a l'agonie, 255 confortable.</summary>
    [FieldOffset(28)] public byte Health;

    public static RenderFeedback For(uint sequence, long packetTimeMs,
                                     float renderMs, float fps, uint dropped) =>
        new()
        {
            Magic = MagicValue,
            Sequence = sequence,
            PacketTimeMs = packetTimeMs,
            RenderMs = renderMs,
            Fps = fps,
            Dropped = dropped,
            Health = (byte)Math.Clamp(
                255 - dropped * 8 - (int)MathF.Max(0, renderMs - 8) * 12, 0, 255),
        };
}
