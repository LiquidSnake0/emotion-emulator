namespace Emotion.Signal;

/// <summary>
/// Le vocabulaire des formes, partage entre la fiche, le paquet et l'unite de rendu.
///
/// POURQUOI UNE LISTE FERMEE ET NON UN NOM LIBRE.
///
/// L'unite de rendu dessine ; elle ne peut pas inventer une forme dont elle n'a pas le
/// trace. Une liste fermee dit exactement ce qu'elle sait faire, et une fiche qui demande
/// autre chose obtient la forme par defaut au lieu d'un carre vide. Un octet suffit, et il
/// voyage dans le mot que chaque source possede deja.
///
/// L'ORDRE PAR DEFAUT N'EST PAS UN CHOIX MUSICAL. Du grave a l'aigu, il ne fait que
/// garantir que six sources restent distinguables tant que personne n'a decide autrement.
/// Des que la fiche parle, elle a le dernier mot.
/// </summary>
public static class SourceShape
{
    public const byte Barres = 1;   // monte — des colonnes espacees, depuis le bas
    public const byte Onde = 2;     // ondule — UNE sinusoide lente qui traverse
    public const byte Orbe = 3;     // enfle — un disque PLEIN qui grandit et rapetisse
    public const byte Comete = 4;   // glisse — une masse qui traverse, avec sa trainee
    public const byte Etoile = 5;   // eclate — un eclat PONCTUEL sur l'attaque
    public const byte Grain = 6;    // scintille — un semis, pour ce qui n'a pas de contour
    public const byte Vague = 7;    // deferle — des cretes serrees, remplies depuis le bas

    public const byte Count = 7;

    /// <summary>
    /// LES LEVRES ONT ETE RETIREES, ET C'EST LE DJ QUI L'A TRANCHE.
    ///
    /// Elles voulaient dire « ce qui chante » : un ovale de filets qui s'ouvrait avec le
    /// niveau et se courbait avec le contour melodique. Personne ne les lisait comme une
    /// bouche — « les levres, ca ressemble a rien ». Une masse qui enfle et retombe se lit
    /// sans apprentissage, et c'est deja ce qui avait ete retenu de la case GRAVE. L'octet
    /// 3 porte donc l'orbe ; une fiche ancienne qui demandait les levres obtient une forme
    /// differente, ce qui est le comportement voulu.
    /// </summary>
    private const int Retire = 0;

    /// <summary>
    /// L'ordre par defaut, du grave a l'aigu, ET IL EXPOSE CHAQUE SOURCE AUTREMENT.
    ///
    /// Six motifs centres et radiaux se ressemblaient tous, parce qu'une case fait trois
    /// fois et demie sa hauteur en largeur : un cercle, un losange et une etoile y
    /// devenaient la meme barre horizontale. Ils sont desormais mesures en pixels, et
    /// surtout composes differemment — en creux, en plein, en ligne qui traverse, en
    /// remplissage par le bas, en semis.
    ///
    /// L'AIGU RECOIT LA VAGUE. Il n'a ni attaque nette ni hauteur stable, seulement une
    /// agitation : un motif centre lui va mal, il lui faut quelque chose qui bouge partout
    /// a la fois.
    /// </summary>
    private static readonly byte[] ParRang = [Barres, Onde, Orbe, Comete, Vague, Grain];

    /// <summary>La forme d'un rang, faute d'indication dans la fiche.</summary>
    public static byte Default(int rank) =>
        (byte)(rank >= 0 && rank < ParRang.Length ? ParRang[rank] : Orbe);

    /// <summary>Le nom d'une forme, pour les ecrans de reglage. Jamais projete.</summary>
    public static string Nommer(byte shape) => shape switch
    {
        Barres => "barres",
        Onde => "onde",
        Orbe => "orbe",
        Comete => "comete",
        Etoile => "etoile",
        Grain => "grain",
        Vague => "vague",
        _ => "?",
    };
}
