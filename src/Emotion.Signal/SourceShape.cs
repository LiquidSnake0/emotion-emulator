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
    public const byte Anneau = 1;   // respire — une masse qui enfle et retombe
    public const byte Onde = 2;     // ondule — un mouvement continu qui traverse
    public const byte Levres = 3;   // s'ouvre — une bouche, pour ce qui chante
    public const byte Losange = 4;  // pulse — des aretes droites, franches
    public const byte Etoile = 5;   // eclate — scintille sur l'attaque
    public const byte Grain = 6;    // scintille — un semis, pour ce qui n'a pas de contour

    public const byte Count = 6;

    /// <summary>La forme d'un rang, faute d'indication dans la fiche.</summary>
    public static byte Default(int rank) =>
        (byte)(rank >= 0 && rank < Count ? rank + 1 : Anneau);

    /// <summary>Le nom d'une forme, pour les ecrans de reglage. Jamais projete.</summary>
    public static string Nommer(byte shape) => shape switch
    {
        Anneau => "anneau",
        Onde => "onde",
        Levres => "levres",
        Losange => "losange",
        Etoile => "etoile",
        Grain => "grain",
        _ => "?",
    };
}
