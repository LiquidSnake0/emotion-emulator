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
    public bool TryStart(ReadOnlySpan<float> spectrogram, ReadOnlySpan<float> seed)
    {
        if (_running || _published) return false;

        // On copie ce dont on a besoin pendant que l'analyse est arretee sur cette image :
        // apres le depart, plus rien de vivant n'est lu par le fil de fond.
        spectrogram.CopyTo(_v);
        seed.CopyTo(_w);

        _running = true;
        if (RunInline) Run(); else Task.Run(Run);
        return true;
    }

    private void Run()
    {
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        for (var it = 0; it < _iterations; it++)
        {
            UpdateH();
            UpdateW();
        }

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
        for (var t = 0; t < _memory; t++)
        {
            Reconstruct(t);

            for (var s = 0; s < _sources; s++)
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
        num = num[.._sources];
        den = den[.._sources];

        for (var i = 0; i < _bins; i++)
        {
            num.Clear();
            den.Clear();

            for (var t = 0; t < _memory; t++)
            {
                float wh = 0;
                for (var s = 0; s < _sources; s++) wh += _w[i * _sources + s] * _h[s * _memory + t];

                var v = _v[i * _memory + t];
                for (var s = 0; s < _sources; s++)
                {
                    var h = _h[s * _memory + t];
                    num[s] += h * v;
                    den[s] += h * wh;
                }
            }

            for (var s = 0; s < _sources; s++)
                _w[i * _sources + s] *= num[s] / (den[s] + Eps);
        }
    }

    private void Reconstruct(int t)
    {
        for (var i = 0; i < _bins; i++)
        {
            float wh = 0;
            for (var s = 0; s < _sources; s++) wh += _w[i * _sources + s] * _h[s * _memory + t];
            _wh[i] = wh;
        }
    }
}
