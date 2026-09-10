namespace Emotion.Signal;

/// <summary>
/// Apprend les profils spectraux des sources, <b>hors du fil d'analyse</b>.
///
/// POURQUOI CETTE CLASSE EXISTE, ET C'EST UNE MESURE QUI L'A DECIDE.
///
/// L'apprentissage tournait dans la meme boucle que l'analyse. Sur un morceau du crate, il
/// coutait <b>268 ms en moyenne et 359 ms au pire</b>, toutes les 1,4 seconde. Une image
/// d'analyse en dure 21. Autrement dit, treize images d'affilee etaient bloquees, deux
/// fois par phrase — puis rattrapees d'un coup. C'est exactement ce que le DJ decrivait a
/// l'ecran : « ca vient en retard comme quand tu regardes un stream sur twitch, ca
/// s'entasse les valeurs les unes apres les autres ».
///
/// Ce cout ne se supprime pas : il faut bien resoudre la factorisation. Ce qu'on peut
/// faire, c'est le <b>sortir du chemin critique</b>. L'analyse continue a suivre l'image
/// courante avec les profils qu'elle a deja, pendant que les prochains se calculent a
/// cote. Un morceau ne change pas de timbre en trois secondes : travailler une phrase de
/// retard sur les profils ne coute rien de perceptible, la ou treize images gelees se
/// voient immediatement.
///
/// C'est le seul parallelisme qui rapporte ici, et il ne rapporte pas parce qu'il calcule
/// plus vite — il calcule exactement aussi vite. Il rapporte parce qu'il calcule
/// <i>ailleurs</i>.
///
/// LA CONCURRENCE, ET POURQUOI IL N'Y A AUCUN VERROU.
///
/// Un seul fil ecrit, un seul fil lit, et jamais la meme chose au meme moment :
/// l'analyse depose une copie du spectrogramme puis n'y touche plus ; l'apprentissage la
/// lit, travaille sur ses propres tableaux, et publie un resultat ; l'analyse le reprend a
/// l'image suivante. Le seul etat partage est un drapeau, et il ne change qu'une fois par
/// sens. Rien a arbitrer, donc rien a verrouiller.
/// </summary>
public sealed class ProfileLearner
{
    private const float Eps = 1e-9f;

    private readonly int _bins;
    private readonly int _sources;
    private readonly int _memory;
    private readonly int _iterations;

    /// <summary>
    /// Combien de sources le prochain apprentissage cherche, et sur combien de trames.
    ///
    /// LE NOMBRE DE SOURCES N'EST PLUS UNE CONSTANTE, ET C'EST LA DEMANDE DU DJ :
    /// « des fois on en a 2, des fois 8, c'est justement ce que le programme est cense me
    /// dire ». La capacite (<c>_sources</c>) reste celle du paquet ; <c>_k</c> est ce qu'on
    /// cherche reellement, et il peut changer d'un disque a l'autre.
    /// </summary>
    private int _k;
    private int _trames;

    /// <summary>Ce que le balayage a mesure pour chaque nombre de sources essaye.</summary>
    public sealed record Bilan(int K, float Reste, float Doublon);
    // PUBLIE D'UN BLOC, JAMAIS PENDANT. Le balayage tourne dans un fil de fond ; une liste
    // qu'on remplit au fur et a mesure se lit a moitie faite depuis /profils, et un bilan a
    // moitie fait ressemble a un choix. On construit a part et l'on echange la reference.
    private volatile IReadOnlyList<Bilan> _bilans = [];
    public IReadOnlyList<Bilan> Bilans => _bilans;

    /// <summary>Le nombre de sources retenu par le dernier balayage, ou zero.</summary>
    public int Choix { get; private set; }

    /// <summary>Le K du dernier apprentissage publie, et s'il venait d'un balayage.</summary>
    public int DernierK { get; private set; }
    public bool DernierEtaitChoix { get; private set; }

    private int _kMin, _kMax, _iterationsBalayage;
    private float _seuilGain, _seuilDoublon;
    private bool _choisir;

    private readonly float[] _v;          // copie du spectrogramme, a nous seuls
    private readonly float[] _w;          // profils en cours d'apprentissage
    private readonly float[] _h;          // activations sur la memoire
    private readonly float[] _wh;
    private readonly float[] _ready;      // profils publies, prets a etre adoptes

    /// <summary>Un apprentissage tourne en ce moment.</summary>
    private volatile bool _running;

    /// <summary>Un resultat attend d'etre adopte par l'analyse.</summary>
    private volatile bool _published;

    public ProfileLearner(int bins, int sources, int memory, int iterations)
    {
        _bins = bins;
        _sources = sources;
        _memory = memory;
        _iterations = iterations;
        _k = sources;
        _trames = memory;

        _v = new float[bins * memory];
        _w = new float[bins * sources];
        _h = new float[sources * memory];
        _wh = new float[bins];
        _ready = new float[bins * sources];

        var rng = new Random(1203);
        for (var i = 0; i < _h.Length; i++) _h[i] = 0.1f + (float)rng.NextDouble() * 0.9f;
    }

    public bool Running => _running;

    /// <summary>
    /// Fait tourner l'apprentissage sur le fil appelant au lieu d'un fil de fond.
    ///
    /// Sert uniquement a la mesure : c'est le seul moyen de comparer les deux regimes sur
    /// le meme morceau et la meme machine. Le chiffre que cela produit est la raison
    /// d'etre de cette classe, et il doit rester reproductible.
    /// </summary>
    public bool RunInline { get; set; }

    /// <summary>Cout moyen d'un apprentissage, en millisecondes. Diagnostic.</summary>
    public double AverageMs => _runs == 0 ? 0 : _totalMs / _runs;

    /// <summary>Pire apprentissage rencontre. C'est lui qui ferait le retard s'il etait en ligne.</summary>
    public double WorstMs { get; private set; }

    public int Runs => _runs;

    private double _totalMs;
    private int _runs;

    /// <summary>
    /// Lance un apprentissage, s'il n'y en a pas deja un. Rend faux si l'on est encore
    /// occupe : c'est un refus normal, pas une erreur. Mieux vaut sauter un apprentissage
    /// que d'en empiler deux — les profils du precedent restent valables.
    /// </summary>
    public bool TryStart(ReadOnlySpan<float> spectrogram, ReadOnlySpan<float> seed,
                         int k = 0, int trames = 0)
    {
        if (_running || _published) return false;
        _k = k <= 0 ? _sources : Math.Min(k, _sources);
        _trames = trames <= 0 ? _memory : Math.Min(trames, _memory);
        _choisir = false;

        // On copie ce dont on a besoin pendant que l'analyse est arretee sur cette image :
        // apres le depart, plus rien de vivant n'est lu par le fil de fond.
        spectrogram.CopyTo(_v);
        seed.CopyTo(_w);

        _running = true;
        if (RunInline) Run(); else Task.Run(Run);
        return true;
    }

    /// <summary>
    /// Cherche COMBIEN de sources le morceau contient, puis les apprend.
    ///
    /// ON DEMANDE AU MORCEAU, ON NE DECIDE PAS. On factorise avec kMin, kMin+1… sources et
    /// l'on regarde deux choses a chaque pas :
    ///
    ///   ce qui reste inexplique   une source de plus doit expliquer une part NEUVE du son ;
    ///                             quand le gain tombe sous le seuil, on en a assez
    ///   les doublons              deux profils qui se ressemblent sont une source coupee en
    ///                             deux : le signe qu'on en a demande trop
    ///
    /// Mesure sur quarante secondes de Passepartout : les gains valaient 4,4 · 1,9 · 1,4 ·
    /// 0,9 · 1,0 · 0,6 · 0,4 point, et un doublon a 0,95 apparaissait a dix. Le coude est a
    /// quatre. Les deux seuils viennent de la et sont a confronter a d'autres disques.
    ///
    /// LE BALAYAGE PART DE LA MEME GRAINE POUR TOUS LES K. Sans cela le reste ne serait pas
    /// comparable d'un K au suivant — une initialisation heureuse a K=5 battrait une
    /// initialisation malheureuse a K=6 et l'on prendrait ce hasard pour un coude.
    /// </summary>
    public bool TryStartChoix(ReadOnlySpan<float> spectrogram, int kMin, int kMax, int trames,
                              int iterationsBalayage = 20, float seuilGain = 0.015f,
                              float seuilDoublon = 0.90f)
    {
        if (_running || _published) return false;
        spectrogram.CopyTo(_v);
        _kMin = Math.Max(1, kMin);
        _kMax = Math.Clamp(kMax, _kMin, _sources);
        _trames = trames <= 0 ? _memory : Math.Min(trames, _memory);
        _iterationsBalayage = iterationsBalayage;
        _seuilGain = seuilGain;
        _seuilDoublon = seuilDoublon;
        _choisir = true;
        _running = true;
        if (RunInline) Run(); else Task.Run(Run);
        return true;
    }

    private void Graine()
    {
        // La meme graine a chaque appel : deux balayages du meme spectrogramme rendent le
        // meme choix, et deux K du meme balayage partagent leurs premieres colonnes.
        var rng = new Random(1203);
        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)rng.NextDouble() * 0.9f;
        for (var i = 0; i < _h.Length; i++) _h[i] = 0.1f + (float)rng.NextDouble() * 0.9f;
    }

    private void Balayer()
    {
        var bilans = new List<Bilan>();
        var choix = _kMin;
        float? restePrecedent = null;
        for (var k = _kMin; k <= _kMax; k++)
        {
            Graine();
            _k = k;
            for (var it = 0; it < _iterationsBalayage; it++) { UpdateH(); UpdateW(); }
            var reste = Reste();
            var doublon = Doublon();
            bilans.Add(new Bilan(k, reste, doublon));

            if (doublon >= _seuilDoublon) break;                             // coupe en deux : trop
            if (restePrecedent is { } rp && rp - reste < _seuilGain) break;  // plus rien de neuf
            choix = k;
            restePrecedent = reste;
        }
        Choix = choix;
        _bilans = bilans;

        // Puis l'apprentissage complet, au nombre retenu, depuis la meme graine.
        Graine();
        _k = choix;
        for (var it = 0; it < _iterations; it++) { UpdateH(); UpdateW(); }
    }

    /// <summary>La part de l'energie que le modele n'explique pas, de 0 a 1.</summary>
    private float Reste()
    {
        double err = 0, tot = 0;
        for (var t = 0; t < _trames; t++)
        {
            Reconstruct(t);
            for (var i = 0; i < _bins; i++)
            {
                var v = _v[i * _memory + t];
                var d = v - _wh[i];
                err += d * d;
                tot += v * v;
            }
        }
        return tot > 0 ? (float)(err / tot) : 0f;
    }

    /// <summary>Le pire cosinus entre deux profils : a un, ce sont le meme.</summary>
    private float Doublon()
    {
        var pire = 0f;
        for (var a = 0; a < _k; a++)
            for (var b = a + 1; b < _k; b++)
            {
                double ps = 0, na = 0, nb = 0;
                for (var i = 0; i < _bins; i++)
                {
                    var x = _w[i * _sources + a];
                    var y = _w[i * _sources + b];
                    ps += x * y; na += x * x; nb += y * y;
                }
                var cos = na > 0 && nb > 0 ? (float)(ps / Math.Sqrt(na * nb)) : 0f;
                if (cos > pire) pire = cos;
            }
        return pire;
    }

    private void Run()
    {
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        if (_choisir)
        {
            Balayer();
        }
        else
        {
            for (var it = 0; it < _iterations; it++)
            {
                UpdateH();
                UpdateW();
            }
        }

        // Les colonnes au-dela de K ne portent rien : on les eteint plutot que de laisser
        // l'ancien profil y survivre et se faire lire comme une source.
        for (var i = 0; i < _bins; i++)
            for (var s2 = _k; s2 < _sources; s2++) _w[i * _sources + s2] = 0f;
        DernierK = _k;
        DernierEtaitChoix = _choisir;

        Array.Copy(_w, _ready, _w.Length);

        var ms = chrono.Elapsed.TotalMilliseconds;
        _totalMs += ms;
        _runs++;
        if (ms > WorstMs) WorstMs = ms;

        // L'ordre compte : le resultat doit etre entierement ecrit avant que le drapeau
        // ne l'annonce, sans quoi l'analyse pourrait adopter des profils a moitie copies.
        _running = false;
        _published = true;
    }

    /// <summary>
    /// Si des profils sont prets, les recopie et rend vrai. Appele par le fil d'analyse,
    /// une fois par image : le cout est une copie de quelques kilo-octets.
    /// </summary>
    public bool TryAdopt(Span<float> destination)
    {
        if (!_published) return false;

        _ready.AsSpan().CopyTo(destination);
        _published = false;
        return true;
    }

    /// <summary>
    /// La regle de Lee et Seung, dans sa forme euclidienne. Elle a une propriete qui la
    /// rend sure ici : les facteurs restent positifs sans qu'on ait a les contraindre,
    /// puisqu'on ne fait que les multiplier par des rapports positifs. Un spectre est une
    /// energie, il n'a pas de partie negative, et une methode qui en produirait decrirait
    /// des sons qui n'existent pas.
    /// </summary>
    private void UpdateH()
    {
        for (var t = 0; t < _trames; t++)
        {
            Reconstruct(t);

            for (var s = 0; s < _k; s++)
            {
                float num = 0, den = 0;
                for (var i = 0; i < _bins; i++)
                {
                    var w = _w[i * _sources + s];
                    num += w * _v[i * _memory + t];
                    den += w * _wh[i];
                }

                _h[s * _memory + t] *= num / (den + Eps);
            }
        }
    }

    private void UpdateW()
    {
        Span<float> num = stackalloc float[16];
        Span<float> den = stackalloc float[16];
        num = num[.._k];
        den = den[.._k];

        for (var i = 0; i < _bins; i++)
        {
            num.Clear();
            den.Clear();

            for (var t = 0; t < _trames; t++)
            {
                float wh = 0;
                for (var s = 0; s < _k; s++) wh += _w[i * _sources + s] * _h[s * _memory + t];

                var v = _v[i * _memory + t];
                for (var s = 0; s < _k; s++)
                {
                    var h = _h[s * _memory + t];
                    num[s] += h * v;
                    den[s] += h * wh;
                }
            }

            for (var s = 0; s < _k; s++)
                _w[i * _sources + s] *= num[s] / (den[s] + Eps);
        }
    }

    private void Reconstruct(int t)
    {
        for (var i = 0; i < _bins; i++)
        {
            float wh = 0;
            for (var s = 0; s < _k; s++) wh += _w[i * _sources + s] * _h[s * _memory + t];
            _wh[i] = wh;
        }
    }
}
