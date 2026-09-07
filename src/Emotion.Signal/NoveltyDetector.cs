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
    private float _rawMean;
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
    private int _sinceLast = int.MaxValue;

    /// <summary>
    /// Ecart minimal entre deux declenchements, en fenetres. Cent quarante valent trois
    /// secondes.
    ///
    /// L'hysteresis seule ne suffisait pas : sur un morceau dont la texture bouge
    /// constamment, la valeur redescend sous le seuil bas assez souvent pour rearmer, et
    /// le balayage repartait toutes les deux secondes. Or c'est un geste d'evenement — il
    /// doit rester rare pour vouloir dire quelque chose. Un effet qui se declenche tout
    /// le temps ne signale plus rien.
    /// </summary>
    private const int Cooldown = 140;

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

        // Seuil adaptatif plutot qu'un facteur constant, et c'est la troisieme tentative.
        //
        // Un facteur fixe s'est revele impossible a regler : a 3,2 la nouveaute ne
        // franchissait jamais son seuil, a 7 elle restait a 0,80 de moyenne et ne
        // redescendait donc jamais assez pour se rearmer. Aucune constante ne peut
        // marcher, parce que l'ecart de texture depend entierement du morceau — une
        // boucle repetitive en produit peu, un montage de samples enormement.
        //
        // On compare donc l'ecart courant a l'ecart <b>habituel de ce morceau</b>, comme
        // le detecteur d'attaques compare le flux a sa propre moyenne. La grandeur
        // devient un rapport, sans unite et sans reglage a refaire par disque.
        _rawMean += (raw - _rawMean) * 0.01f;
        var ratio = _rawMean > 1e-6f ? raw / _rawMean : 0f;

        var target = Clamp01((ratio - 1f) * 0.8f);
        Level += (target - Level) * 0.25f;

        // Seuil a hysteresis : on declenche haut et on se rearme bas. Un seuil unique
        // ferait clignoter le declenchement pendant tout le temps ou la valeur oscille
        // autour de lui — or une voix dure plusieurs secondes.
        Onset = false;
        if (_sinceLast < int.MaxValue) _sinceLast++;

        // Seuil releve apres ecoute : a 0,30 le balayage partait trop souvent. Une
        // nouveaute doit etre franche pour meriter son geste.
        if (_armed && _sinceLast >= Cooldown && Level > 0.45f)
        {
            Onset = true;
            _armed = false;
            _sinceLast = 0;
        }
        else if (!_armed && Level < 0.20f)
        {
            _armed = true;
        }
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
