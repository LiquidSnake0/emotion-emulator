namespace Emotion.Signal;

/// <summary>
/// Ce qu'une source finit par savoir d'elle-meme, a son propre rythme.
///
/// L'IDEE, ET POURQUOI ELLE NE PEUT PAS ETRE SYNCHRONE.
///
/// Une source n'est pas identifiable des sa premiere image. Une basse qui tient toute la
/// boucle se decrit en deux secondes ; un instrument qui n'entre qu'au refrain mettra une
/// minute a se montrer assez souvent pour qu'on puisse dire quoi que ce soit de lui. Rien
/// ne justifie d'attendre le second pour publier le premier — et rien ne justifie non plus
/// d'attendre l'un ou l'autre pour publier le niveau et le contour, qui sont justes des la
/// premiere image.
///
/// Chaque source porte donc <b>sa propre maturite</b>, qui monte quand elle joue et
/// stagne quand elle se tait. Le paquet part a cadence fixe quoi qu'il arrive ; ce qui
/// change d'une image a l'autre, c'est la quantite de choses que chaque source est en
/// mesure d'affirmer. Le renderer lit une confiance et decide seul de ce qu'il en fait :
/// une source a peine connue reste une forme anonyme, une source mure peut recevoir un
/// traitement propre.
///
/// CE QU'ON NE FAIT PAS ICI.
///
/// On ne nomme rien. Cette classe produit une empreinte et une confiance, pas un mot :
/// decider que la source 3 est un piano supposerait de reconnaitre un piano, et la bande 3
/// porte un saxophone au disque suivant. Le nom viendra d'ailleurs — de la fiche du crate,
/// ou de Selim lui-meme — et se posera sur une empreinte assez stable pour le supporter.
/// L'empreinte est ce qui permet ce rapprochement ; elle n'en est pas le resultat.
/// </summary>
public sealed class SourceIdentity
{
    /// <summary>Sous ce niveau, la source ne joue pas et n'apprend donc rien d'elle-meme.</summary>
    private const float Audible = 0.12f;

    /// <summary>
    /// Observations qu'il faut avoir accumulees pour une confiance pleine.
    ///
    /// Deux cents images actives, soit un peu plus de quatre secondes de jeu effectif.
    /// Assez pour qu'une empreinte tienne sur plusieurs notes et plusieurs mesures, ce qui
    /// est la duree en dessous de laquelle une seule note colorerait tout le portrait.
    /// </summary>
    private const int Mature = 200;

    /// <summary>Inertie de l'empreinte. Lente : c'est un portrait, pas une mesure.</summary>
    private const float Inertia = 0.02f;

    /// <summary>
    /// Bornes de la dispersion, <b>relevees sur le repertoire et non choisies</b>.
    ///
    /// Sur un morceau du crate, les six registres se sont ranges entre 0,087 et 0,229 : la
    /// bande la plus tenue en bas, celles que plusieurs instruments se partagent en haut.
    /// Un premier reglage devine — une netteté en 1 - 6·dispersion — mettait les six a
    /// zero, y compris la plus stable : il declarait tout inconnu et n'apprenait donc
    /// jamais rien.
    ///
    /// Les deux bornes encadrent l'etendue mesuree. En dessous de <see cref="Tight"/>, une
    /// source est aussi tenue que la meilleure du disque ; au-dessus de <see cref="Loose"/>,
    /// elle est aussi partagee que la pire, et ne merite aucun nom.
    /// </summary>
    private const float Tight = 0.06f;
    private const float Loose = 0.24f;

    private int _observations;
    private float _spread;

    /// <summary>Dispersion moyenne des observations autour du portrait. Diagnostic.</summary>
    public float Spread => _spread;

    /// <summary>Observations accumulees. Diagnostic.</summary>
    public int Observations => _observations;

    /// <summary>Brillance moyenne de la source, 0 sourde, 1 claire.</summary>
    public float Brightness { get; private set; } = 0.5f;

    /// <summary>
    /// A quel point l'energie est etalee dans la bande, 0 une raie franche, 1 un souffle.
    /// C'est ce qui separe un instrument tenu d'une texture, la ou la brillance seule les
    /// confondrait.
    /// </summary>
    public float Texture { get; private set; } = 0.5f;

    /// <summary>
    /// A quel point on peut se fier a l'empreinte, 0 a 1.
    ///
    /// Elle croit avec le nombre d'observations et decroit avec leur dispersion : une
    /// source vue longtemps mais jamais deux fois pareille reste inconnue, et c'est le bon
    /// resultat — cela veut dire que plusieurs instruments passent dans cette bande.
    /// </summary>
    public float Confidence { get; private set; }

    /// <summary>
    /// Classe attribuee de l'exterieur, 0 tant que personne n'a nomme cette source.
    ///
    /// L'analyse ne la calcule pas : elle la transporte. C'est le seul champ du systeme
    /// dont la valeur vienne d'une decision humaine ou de la fiche, et il part au GPU
    /// comme les autres.
    /// </summary>
    public byte Label { get; set; }

    public void Feed(ReadOnlySpan<float> spectrum, int lo, int hi, float level)
    {
        if (level < Audible || hi <= lo) return;

        double sum = 0, weighted = 0, sumSq = 0;
        var peak = 0f;
        for (var i = lo; i < hi; i++)
        {
            var v = spectrum[i];
            sum += v;
            weighted += v * (i - lo);
            sumSq += (double)v * v;
            if (v > peak) peak = v;
        }

        if (sum < 1e-7) return;

        var width = MathF.Max(hi - lo - 1, 1);
        var brightness = (float)(weighted / sum) / width;

        // Rapport entre l'energie moyenne et l'energie de crete : une raie franche
        // concentre tout sur un bin, un souffle repartit tout. C'est une platitude, en
        // moins couteux qu'une moyenne geometrique — et cette boucle tourne par image et
        // par source.
        var mean = (float)(sum / (hi - lo));
        var texture = peak > 1e-7f ? Clamp01(mean / peak) : 0.5f;

        // Dispersion : de combien chaque observation s'ecarte du portrait deja forme. Une
        // source qui change tout le temps ne merite pas de confiance, meme vue souvent.
        var drift = MathF.Abs(brightness - Brightness) + MathF.Abs(texture - Texture);
        _spread += (drift - _spread) * Inertia;

        Brightness += (brightness - Brightness) * Inertia;
        Texture += (texture - Texture) * Inertia;

        if (_observations < Mature) _observations++;

        // La maturite monte avec l'experience, la nettete descend avec l'instabilite. Le
        // produit des deux : il faut avoir beaucoup vu ET avoir vu la meme chose.
        var maturity = _observations / (float)Mature;
        var sharpness = Clamp01(1f - (_spread - Tight) / (Loose - Tight));
        Confidence = maturity * sharpness;
    }

    public void Reset()
    {
        _observations = 0;
        _spread = 0f;
        Brightness = 0.5f;
        Texture = 0.5f;
        Confidence = 0f;
        Label = 0;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
