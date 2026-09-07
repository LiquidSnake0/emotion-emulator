namespace Emotion.Signal;

/// <summary>
/// Detecte l'arrivee d'un element qui n'etait pas la, sans savoir ce que c'est.
///
/// Les autres detecteurs cherchent chacun une chose precise : une attaque dans le grave,
/// une dans le medium, un changement de profil de hauteurs. Ils ne voient donc que ce
/// qu'on leur a appris a voir. Une voix qui entre en disant « sunrise » n'est ni un kick,
/// ni un clap, ni un changement d'accord — elle est <b>invisible</b> pour eux, alors que
/// l'oreille l'attrape immediatement.
///
/// Celui-ci ne cherche rien de particulier. Il compare la texture du moment a celle des
/// dernieres secondes et signale l'ecart. Une voix, un sample, une nappe qui entre, un
/// filtre qu'on ouvre, un break : tout ce qui change la couleur du son passe ici,
/// precisement parce qu'on n'a pas dit quoi chercher.
///
/// C'est aussi la brique de la lecture de structure : un couplet et un refrain ne
/// different pas par leurs attaques mais par leur texture, et une frontiere de section
/// est un pic de nouveaute qui dure.
/// </summary>
public sealed class NoveltyDetector
{
    /// <summary>
    /// Longueur de la memoire, en fenetres. Cent quatre-vingts valent environ quatre
    /// secondes : assez long pour qu'une boucle de deux mesures soit consideree comme
    /// le fond habituel, assez court pour qu'un changement de section ressorte.
    /// </summary>
    private const int Memory = 180;

    private readonly float[] _mean = new float[VisualFrame.BandCount];
    private int _seen;

    /// <summary>Coefficient d'oubli, deduit de la memoire.</summary>
    private readonly float _alpha = 1f / Memory;

    /// <summary>Ecart lisse a la texture habituelle, 0 a 1.</summary>
    public float Level { get; private set; }

    /// <summary>
    /// Vrai sur la fenetre ou la nouveaute franchit son seuil, une seule fois par
    /// evenement. Une impulsion, comme une attaque — sinon l'effet resterait allume
    /// tout le temps que dure la voix.
    /// </summary>
    public bool Onset { get; private set; }

    private bool _armed = true;

    /// <summary>
    /// Nourrit le detecteur du profil de bandes courant.
    /// </summary>
    public void Feed(float[] bands)
    {
        if (bands.Length != VisualFrame.BandCount) return;

        // Distance a la texture moyenne, avant mise a jour : on compare le present a ce
        // qui precede, jamais a lui-meme.
        var d = 0f;
        for (var i = 0; i < bands.Length; i++)
        {
            var diff = bands[i] - _mean[i];
            d += diff * diff;
        }
        var raw = MathF.Sqrt(d / bands.Length);

        // Moyenne glissante exponentielle : pas d'historique a garder, et l'oubli est
        // progressif plutot que brutal comme le serait une fenetre glissante.
        for (var i = 0; i < bands.Length; i++)
            _mean[i] += (bands[i] - _mean[i]) * _alpha;

        if (_seen < Memory)
        {
            // Pendant le remplissage, tout parait nouveau : on se tait.
            _seen++;
            Level = 0f;
            Onset = false;
            return;
        }

        // Lissage court : la distance brute tremble d'une fenetre a l'autre, et un
        // visuel qui suivrait ce tremblement scintillerait.
        // Facteur releve apres mesure : sur instamata la nouveaute ne franchissait
        // jamais son seuil, parce que l'ecart de texture d'un morceau construit sur une
        // boucle reste faible en valeur absolue. C'est le rapport qui compte, pas
        // l'amplitude brute.
        var target = Clamp01(raw * 7f);
        Level += (target - Level) * 0.25f;

        // Seuil a hysteresis : on declenche haut et on se rearme bas. Un seuil unique
        // ferait clignoter le declenchement pendant tout le temps ou la valeur oscille
        // autour de lui — or une voix dure plusieurs secondes.
        Onset = false;
        if (_armed && Level > 0.30f)
        {
            Onset = true;
            _armed = false;
        }
        else if (!_armed && Level < 0.16f)
        {
            _armed = true;
        }
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
