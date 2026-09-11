namespace Emotion.Signal;

/// <summary>
/// La gamme du disque, tiree de la fiche Camelot du crate — un prior, jamais une contrainte.
///
/// « SAVOIR QUE C'EST 9A DE LA PART DU CRATE PEUT NOUS AIDER, COMME LE BPM ? » Mesure avant
/// d'y croire : le chromagramme du moteur retrouve la fiche exactement sur quatre titres sur
/// dix, au relatif ou a la quinte sur deux, pas sur quatre — des cas serres en lo-fi. C'est
/// donc un point de depart, employe comme le BPM de la fiche : une preference douce sur les
/// positions des gabarits, et la contradiction dite quand le chromagramme n'est pas
/// d'accord.
///
/// CE QU'ELLE RAPPORTE AU RENDU. Avec la gamme connue, la note courante d'une source devient
/// un <b>degre</b> — tonique, quinte, tierce — au lieu d'une hauteur en hertz. « Au boom la
/// tonique, au tchak la quinte. » Et comme le DJ mixe en Camelot (9A → 9B, 10A, 8A), les
/// degres restent compatibles d'un disque au suivant : la couleur qui va avec un degre survit
/// a la transition.
/// </summary>
public static class Gamme
{
    /// <summary>Le degre « hors gamme », et « inconnu » quand rien n'est su.</summary>
    public const int HorsGamme = 7;
    public const int Inconnu = 15;

    private static readonly int[] Majeurs = [11, 6, 1, 8, 3, 10, 5, 0, 7, 2, 9, 4];   // 1B..12B : B F# Db Ab Eb Bb F C G D A E
    private static readonly int[] IntervallesMajeur = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] IntervallesMineur = [0, 2, 3, 5, 7, 8, 10];

    /// <summary>
    /// La tonique (classe de hauteur, do = 0) et le mode d'un tag Camelot, ou null s'il
    /// n'en est pas un. 8A = la mineur, 8B = do majeur.
    /// </summary>
    public static (int Tonique, bool Mineur)? Lire(string? camelot)
    {
        if (string.IsNullOrWhiteSpace(camelot)) return null;
        var t = camelot.Trim().ToUpperInvariant();
        var lettre = t[^1];
        if (lettre != 'A' && lettre != 'B' || !int.TryParse(t[..^1], out var n) || n < 1 || n > 12) return null;
        var majeur = Majeurs[n - 1];
        return lettre == 'B' ? (majeur, false) : ((majeur + 9) % 12, true);   // le relatif mineur : une sixte au-dessus
    }

    /// <summary>Les classes de hauteur de la gamme, do = 0, ou null.</summary>
    public static bool[]? Classes(string? camelot)
    {
        if (Lire(camelot) is not { } g) return null;
        var m = new bool[12];
        foreach (var i in g.Mineur ? IntervallesMineur : IntervallesMajeur) m[(g.Tonique + i) % 12] = true;
        return m;
    }

    /// <summary>
    /// Le degre d'une classe de hauteur dans la gamme : 0 la tonique, 4 la quinte, 6 la
    /// sensible ; <see cref="HorsGamme"/> si elle n'y est pas.
    /// </summary>
    public static int Degre(string? camelot, int classe)
    {
        if (Lire(camelot) is not { } g) return Inconnu;
        var intervalles = g.Mineur ? IntervallesMineur : IntervallesMajeur;
        var relatif = ((classe - g.Tonique) % 12 + 12) % 12;
        var d = Array.IndexOf(intervalles, relatif);
        return d >= 0 ? d : HorsGamme;
    }

    /// <summary>Le nom d'un degre, en chiffres romains, pour l'ecran et la sonde.</summary>
    public static string Nom(int degre) => degre switch
    {
        0 => "I", 1 => "II", 2 => "III", 3 => "IV", 4 => "V", 5 => "VI", 6 => "VII",
        HorsGamme => "·", _ => "",
    };
}
