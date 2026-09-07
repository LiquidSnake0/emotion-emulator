namespace Emotion.Signal;

/// <summary>
/// Decide si une montee du flux spectral est une attaque.
///
/// Le seuil est <b>adaptatif</b>, et c'est indispensable : un seuil fixe marcherait sur
/// un morceau et raterait tout le suivant, puisque le crate va d'un ambient feutre a des
/// batteries seches. On compare donc chaque valeur a la moyenne recente plutot qu'a une
/// constante — le detecteur suit le morceau au lieu d'etre regle pour lui.
/// </summary>
public sealed class OnsetDetector
{
    /// <summary>
    /// Combien de fenetres on attend avant de conclure qu'on etait sur un sommet.
    ///
    /// <b>Une fenetre, soit 21 ms.</b> La valeur precedente en valait trois, et ce
    /// choix etait mauvais : additionne aux 64 ms de la separation harmonique, il
    /// portait le retard total a 128 ms entre le son et l'image. Or l'oeil decroche
    /// vers 40 ms — un eclair arrivant un huitieme de seconde apres le clap ne parait
    /// plus lie a lui du tout.
    ///
    /// Une seule fenetre de recul suffit a distinguer un sommet d'une montee : il faut
    /// juste que la valeur suivante soit plus basse. Deux ou trois filtraient un peu
    /// mieux le bruit, mais un filtrage qu'on paie en desynchronisation n'en vaut pas
    /// la peine sur un visuel.
    /// </summary>
    public const int Lookahead = 1;

    private readonly float[] _window = new float[Lookahead * 2 + 1];
    private int _filled;

    private const int History = 43;         // ~0,9 s a 48 kHz par fenetres de 1024
    private readonly float[] _recent = new float[History];
    private int _n;
    private int _sinceLast = int.MaxValue;

    /// <summary>
    /// Combien de fenetres au minimum entre deux attaques.
    ///
    /// Regle sur le crate et non dans l'abstrait : il vit entre 82 et 97 BPM, soit un
    /// temps de 620 a 730 ms, et une croche de 310 a 365 ms. La valeur par defaut de
    /// vingt fenetres vaut 426 ms : le seuil passe entre les deux, donc on garde le
    /// temps et on refuse la croche.
    ///
    /// Deux mesures sur instamata, 87 BPM, ont conduit ici. A six fenetres le detecteur
    /// voyait 41 attaques en dix secondes, soit 341 BPM — quatre par temps. A quatorze,
    /// l'ecart median tombait a 346 ms, soit exactement la croche.
    ///
    /// C'est un reglage tire du repertoire, pas d'un principe general, d'ou le
    /// parametre : les charleys ont le droit d'aller plus vite que le temps.
    /// </summary>
    private readonly int _minGap;

    /// <summary>
    /// Marge au-dessus de la moyenne recente. Trop bas, chaque nappe declenche ;
    /// trop haut, un morceau feutre ne declenche jamais.
    /// </summary>
    private const float Margin = 1.8f;

    /// <param name="minGap">
    /// Ecart minimal en fenetres. La valeur par defaut est reglee sur le temps du crate ;
    /// les charleys, eux, ont le droit d'aller au double de vitesse.
    /// </param>
    public OnsetDetector(int minGap = 20) => _minGap = minGap;

    /// <summary>Moyenne recente du flux, base du seuil. Diagnostic.</summary>
    public float Baseline { get; private set; }

    /// <summary>Seuil qu'il faut depasser pour declencher. Diagnostic.</summary>
    public float Threshold => Baseline * Margin;

    /// <summary>
    /// Nourrit le detecteur et dit si une attaque tombe <b>a l'instant juge</b>,
    /// c'est-a-dire il y a <see cref="Lookahead"/> fenetres.
    /// </summary>
    public bool Feed(float flux)
    {
        // Tampon glissant : la valeur du milieu est celle qu'on juge, et on connait
        // donc ce qui vient apres elle.
        for (var i = 0; i < _window.Length - 1; i++) _window[i] = _window[i + 1];
        _window[^1] = flux;

        if (_filled < _window.Length)
        {
            _filled++;
            Push(flux);
            Baseline = Mean();
            return false;
        }

        _sinceLast = _sinceLast == int.MaxValue ? _minGap : _sinceLast + 1;

        var mean = Mean();
        Baseline = mean;
        Push(flux);

        // Tant que l'historique n'est pas rempli, on ne decide rien : les premieres
        // fenetres apres le lancement declencheraient toutes.
        if (_n < History) return false;
        if (_sinceLast < _minGap) return false;
        if (mean <= 0f) return false;

        var candidate = _window[Lookahead];
        if (candidate <= mean * Margin) return false;

        // Maximum local strict : rien d'aussi haut ni avant ni apres. C'est ce qui
        // distingue une attaque d'une montee progressive, et c'est ce qui manquait.
        for (var i = 0; i < _window.Length; i++)
            if (i != Lookahead && _window[i] >= candidate) return false;

        _sinceLast = 0;
        return true;
    }

    private void Push(float v)
    {
        _recent[_n % History] = v;
        if (_n < int.MaxValue) _n++;
    }

    private float Mean()
    {
        var count = Math.Min(_n, History);
        if (count == 0) return 0f;
        var sum = 0f;
        for (var i = 0; i < count; i++) sum += _recent[i];
        return sum / count;
    }
}
