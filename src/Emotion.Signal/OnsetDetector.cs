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

    // ZERO A ETE TESTE, ET IL COUTE PLUS QU'IL NE RAPPORTE.
    //
    // Supprimer l'anticipation retirerait 21,3 ms du retard total, ce qui est la plus grosse
    // economie disponible dans toute la chaine. Mais sans la valeur suivante, on ne peut plus
    // distinguer un sommet d'une montee : le detecteur declenche sur la pente et manque le
    // pic. Sur trente secondes du repertoire, les detections tombent de 1003 a 683 — un tiers
    // perdu — les intervalles justes de 31 a 23 %, et le verrouillage de la grille de 85 a
    // 70 %.
    //
    // Quinze points de verrouillage pour 21 ms : le marche est mauvais, parce que c'est
    // justement le verrouillage qui permet a l'horloge de <b>predire</b> le kick, et donc
    // d'annuler ces 21 ms et bien davantage. Raccourcir ici casserait ce qui compense.

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
    private int _minGap;

    /// <summary>
    /// Regle l'ecart minimal sur le tempo mesure, plutot que sur une constante.
    ///
    /// POURQUOI UNE CONSTANTE NE POUVAIT PAS MARCHER.
    ///
    /// Vingt fenetres valent 427 ms, ce qui a ete regle sur un crate vivant entre 82 et
    /// 97 BPM. Mais 427 ms represente 0,63 temps a 88 BPM et 0,58 a 82 : dans les deux cas,
    /// le detecteur laisse passer des attaques qui ne sont pas sur le temps. La mesure le
    /// confirmait — sur un extrait, <b>24 intervalles sur 39 valaient 0,75 temps</b> et
    /// neuf seulement un temps entier, ce qui donne a l'oeil un « 1.2..3..4 » au lieu d'un
    /// « 1..2..3..4 ».
    ///
    /// L'ecart doit donc se compter en temps, pas en millisecondes : c'est une grandeur
    /// musicale. Tant que le tempo n'est pas accroche, on garde la constante — mieux vaut
    /// un filtre approximatif qu'un filtre calcule sur un tempo invente.
    /// </summary>
    public void Suivre(float? bpm, float frameMs, float partDeTemps)
    {
        if (bpm is not { } b || b <= 0f) return;
        var temps = 60_000f / b;
        _minGap = Math.Max(2, (int)MathF.Round(temps * partDeTemps / frameMs));
    }

    /// <summary>Ecart minimal en vigueur, en fenetres. Diagnostic.</summary>
    public int MinGap => _minGap;

    /// <summary>
    /// Marge au-dessus de la moyenne recente. Trop bas, chaque nappe declenche ;
    /// trop haut, un morceau feutre ne declenche jamais.
    /// </summary>
    public float Margin { get; set; } = 1.8f;

    /// <param name="minGap">
    /// Ecart minimal en fenetres. La valeur par defaut est reglee sur le temps du crate ;
    /// les charleys, eux, ont le droit d'aller au double de vitesse.
    /// </param>
    /// <summary>Ecart minimal en millisecondes, pour le diagnostic.</summary>
    public static float MinGapMs => 20 * SpectrumAnalyzer.Window * 1000f / 48_000f;

    public OnsetDetector(int minGap = 20) => _minGap = minGap;

    /// <summary>Moyenne recente du flux, base du seuil. Diagnostic.</summary>
    public float Baseline { get; private set; }

    /// <summary>Seuil qu'il faut depasser pour declencher. Diagnostic.</summary>
    public float Threshold => Baseline * Margin;

    // UNE HYPOTHESE TESTEE ET REFUTEE : LE SEUIL GUIDE PAR LA GRILLE.
    //
    // L'idee etait tentante. Un morceau a un tempo ; quand la grille est accrochee, on sait
    // ou le prochain temps doit tomber. Plutot que de juger chaque fenetre isolement avec le
    // meme seuil partout, on l'abaissait pres du temps attendu et on le laissait haut
    // ailleurs — une frappe etouffee au bon endroit passe, une bosse de meme amplitude entre
    // deux temps ne passe pas. C'est ce que fait l'oreille.
    //
    // La mesure a dit non, et de deux facons a la fois. A dix et quinze pour cent de faveur,
    // elle ne changeait <b>aucune decision</b> : mêmes 28 % d'intervalles justes, mêmes 20 %
    // de frappes bien calees, meme ecart de phase. A trente pour cent, elle changeait des
    // decisions et les degradait — les frappes bien calees tombaient a 16 %, le verrouillage
    // de 83 a 80 %.
    //
    // LA RAISON EST INSTRUCTIVE. L'ecart de phase moyen entre les frappes et la grille est
    // de 0,25 temps sur ce repertoire : la grille elle-meme est mal calee. Favoriser sa
    // position revient donc a favoriser une position fausse, et l'on ne peut pas se servir
    // de la grille pour ameliorer les frappes qui servent a la caler — pas tant qu'elle
    // n'est pas deja juste.
    //
    // Ce qui a marche, lui, ne demandait rien a la grille : compter l'ecart minimal en
    // temps plutot qu'en millisecondes. Voir <see cref="Suivre"/>.

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
        if (Median) return Mediane(count);
        var sum = 0f;
        for (var i = 0; i < count; i++) sum += _recent[i];
        return sum / count;
    }

    /// <summary>
    /// Prend la mediane de l'historique plutot que sa moyenne.
    ///
    /// La difference n'est pas cosmetique. La moyenne est <b>tiree vers le haut par les
    /// pics eux-memes</b> : chaque attaque detectee remonte la reference qui servira a
    /// juger la suivante, si bien qu'une salve de frappes fortes eteint le detecteur
    /// juste apres. La mediane, elle, ignore les valeurs extremes par construction — elle
    /// decrit le fond sonore, ce qui est exactement ce a quoi une attaque doit etre
    /// comparee. C'est le choix de Dixon (2006) et de Bello (2005).
    ///
    /// Quarante-trois valeurs a trier par fenetre, soit une quarantaine de fois par
    /// seconde : le cout ne se mesure pas.
    /// </summary>
    public bool Median { get; set; }

    private readonly float[] _tri = new float[History];

    private float Mediane(int count)
    {
        Array.Copy(_recent, _tri, count);
        Array.Sort(_tri, 0, count);
        return count % 2 == 1 ? _tri[count / 2] : (_tri[count / 2 - 1] + _tri[count / 2]) * 0.5f;
    }
}
