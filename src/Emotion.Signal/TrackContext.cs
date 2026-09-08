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
    /// <summary>
    /// Le phenomene a projeter, deduit de la famille et calcule une seule fois.
    ///
    /// C'etait une propriete calculee, donc une comparaison de chaines a chaque message
    /// vers l'unite de rendu. Une valeur qui ne change qu'au changement de face n'a rien
    /// a faire sur un chemin parcouru quarante-sept fois par seconde.
    /// </summary>
    public Scene Scene { get; } = Scene.ForFamily(Family);

    /// <summary>
    /// Nombre de cotes du polygone qui porte la voix, tire du <b>chiffre</b> Camelot.
    ///
    /// Le tag traversait tout le systeme sans rien piloter : il etait recu, transporte,
    /// documente dans trois fichiers, et aucun code ne le lisait. La forme des anneaux
    /// concentriques etait donc la meme pour tous les disques.
    ///
    /// La correspondance est directe — Camelot 8 donne un octogone — parce qu'elle
    /// s'explique en une phrase et se verifie d'un coup d'oeil. Les positions 1 a 3
    /// donnent toutes un triangle, faute de polygone a moins de trois cotes.
    ///
    /// <b>Cette regle vivait en double.</b> Le renderer la calculait de son cote a partir
    /// du tag, et rien ne garantissait que les deux implementations restent d'accord —
    /// elles avaient d'ailleurs deja diverge. Le calcul appartient desormais a la fiche,
    /// et le rendu la lit.
    /// </summary>
    public int Sides { get; } = SidesFor(Camelot);

    /// <summary>
    /// Mode mineur, tire de la <b>lettre</b> Camelot. A pour mineur, B pour majeur.
    /// Vrai par defaut : le bac est tres majoritairement mineur, et une valeur inconnue
    /// vaut mieux ressembler au cas frequent qu'a l'exception.
    /// </summary>
    public bool Minor { get; } = !(Camelot ?? "").TrimEnd().EndsWith('B');

    private static int SidesFor(string? camelot)
    {
        var s = (camelot ?? "").Trim();
        var digits = 0;
        foreach (var c in s)
        {
            if (!char.IsDigit(c)) break;
            digits = digits * 10 + (c - '0');
        }

        // Hors roue — tag vide ou illisible — on rend l'hexagone, valeur neutre qui ne
        // ressemble a aucune position particuliere.
        if (digits < 1 || digits > 12) return 6;
        return Math.Max(3, digits);
    }

    /// <summary>
    /// La couleur decomposee, calculee une seule fois a la construction.
    ///
    /// Elle l'etait auparavant a chaque message vers l'unite de rendu, soit une chaine
    /// analysee quarante-sept fois par seconde pour une valeur qui ne change qu'au
    /// changement de face. La mesure etait sans appel : 22 microsecondes par message,
    /// contre moins d'une une fois le calcul sorti du chemin chaud.
    /// </summary>
    public (byte R, byte G, byte B) Rgb { get; } = ParseHex(ColorHex);

    private static (byte, byte, byte) ParseHex(string? hex)
    {
        var s = (hex ?? "").TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                                            null, out var v))
            return (110, 110, 110);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>
    /// Rien ne joue encore. Le renderer doit pouvoir demarrer sans morceau : au
    /// lancement, et entre deux disques.
    /// </summary>
    public static readonly TrackContext Silence =
        new("—", "—", "", "", "", "#6E6E6E", null);
}
