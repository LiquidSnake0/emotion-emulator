namespace Emotion.Signal;

/// <summary>
/// Les instruments qui ne frappent pas, suivis registre par registre.
/// </summary>
/// <param name="Low">niveau du grave — agregat des deux premiers registres.</param>
/// <param name="Mid">niveau du medium.</param>
/// <param name="High">niveau de l'aigu.</param>
/// <param name="Levels">niveau de chacun des <see cref="Registers"/> registres.</param>
/// <param name="Pitches">position dans chaque registre, 0 en bas, 1 en haut.</param>
/// <param name="Hits">un bit par registre : celui qui vient d'etre attaque.</param>
public readonly record struct Voices(
    float Low, float Mid, float High,
    bool LowHit, bool MidHit, bool HighHit,
    float LowPitch = 0.5f, float MidPitch = 0.5f, float HighPitch = 0.5f,
    float[]? Levels = null, float[]? Pitches = null, int Hits = 0)
{
    /// <summary>
    /// Nombre de registres tonals suivis separement.
    ///
    /// TROIS NE SUFFISAIENT PAS, ET LA RAISON EST MUSICALE. Un piano et un saxophone qui
    /// jouent dans la meme octave tombaient dans le meme registre : ils devenaient une
    /// seule grandeur, donc une seule forme, et tout ce que l'oreille distingue entre eux
    /// disparaissait. Selim l'a dit ainsi : « a l'oreille je vois tellement de choses et
    /// t'en affiches pas autant ».
    ///
    /// Six bandes d'une octave chacune, de 100 Hz a 6,4 kHz. Assez fin pour que deux
    /// instruments d'un meme morceau tombent rarement ensemble, assez large pour qu'un
    /// seul n'occupe pas trois bandes a lui tout seul.
    ///
    /// <b>Et aucune n'est nommee.</b> On ne decide pas que la bande 3 est un piano — elle
    /// est la bande 3. Ce qui joue dedans change d'un disque a l'autre, et pretendre le
    /// contraire ferait afficher un piano la ou passe un saxophone.
    /// </summary>
    public const int Registers = 6;

    public static readonly Voices None = new(0f, 0f, 0f, false, false, false);

    public bool Any => LowHit || MidHit || HighHit;

    /// <summary>Le registre donne vient-il d'etre attaque.</summary>
    public bool HitAt(int i) => (Hits & (1 << i)) != 0;

    public float LevelAt(int i) => Levels is { } l && i < l.Length ? l[i] : 0f;
    public float PitchAt(int i) => Pitches is { } p && i < p.Length ? p[i] : 0.5f;
}

/// <summary>
/// Suit ce qui joue sans frapper, dans chaque registre pris a part.
/// </summary>
public sealed class VoiceTracker
{
    private const int N = Voices.Registers;
    private const float FrameSeconds = 1024f / 48_000f;

    private readonly int[] _edges;
    private readonly float[] _level = new float[N];
    private readonly float[] _prev = new float[N];
    private readonly float[] _peak = new float[N];
    private readonly float[] _cible = new float[N];
    private readonly Damper[] _pitch = new Damper[N];
    private readonly OnsetDetector[] _onsets = new OnsetDetector[N];

    // Deux jeux publies a tour de role, pour ne rien allouer par image : le consommateur
    // en ligne a fini de lire l'un avant que l'autre ne soit reecrit. Meme raison que pour
    // les douze bandes du spectre.
    private readonly float[][] _levelPool = [new float[N], new float[N]];
    private readonly float[][] _pitchPool = [new float[N], new float[N]];
    private int _turn;

    public VoiceTracker(int sampleRate, int window)
    {
        var binHz = sampleRate / (float)window;

        // Bandes logarithmiques parce que l'oreille entend des rapports : de 100 a 200 il
        // y a le meme intervalle que de 3200 a 6400.
        _edges = new int[N + 1];
        for (var i = 0; i <= N; i++)
            _edges[i] = Math.Max(i + 1, (int)(100f * MathF.Pow(2f, i) / binHz));
        for (var i = 1; i <= N; i++)
            if (_edges[i] <= _edges[i - 1]) _edges[i] = _edges[i - 1] + 1;

        for (var r = 0; r < N; r++)
        {
            _peak[r] = 1e-3f;
            _cible[r] = 0.5f;

            // Le contour glisse d'autant plus vite que le registre est haut : une note
            // aigue change plus souvent qu'une note grave.
            _pitch[r] = new Damper(6f + r * 1.2f, 0.5f);

            // Huit fenetres, soit 170 ms : deux notes par temps restent distinctes a
            // 87 BPM, la ou la limite des percussions en autoriserait une seule.
            _onsets[r] = new OnsetDetector(minGap: 8);
        }
    }

    public Voices Feed(ReadOnlySpan<float> spectre)
    {
        var levels = _levelPool[_turn];
        var pitches = _pitchPool[_turn];
        _turn ^= 1;

        var hits = 0;

        for (var r = 0; r < N; r++)
        {
            var lo = _edges[r];
            var hi = Math.Min(_edges[r + 1], spectre.Length);
            if (hi <= lo) { levels[r] = 0f; pitches[r] = _pitch[r].Value; continue; }

            // OU JOUE CE REGISTRE, ET PAS SEULEMENT COMBIEN.
            //
            // Un niveau ne decrit aucun mouvement : quand une melodie monte, une bande
            // baisse et sa voisine monte — deux faits independants dont aucun ne porte le
            // geste. Le centre de gravite, lui, se deplace, et une forme peut le suivre.
            double sum = 0, weighted = 0;
            for (var i = lo; i < hi; i++)
            {
                var v = spectre[i];
                sum += v;
                weighted += v * MathF.Log2(MathF.Max(i, 1));
            }

            var brut = (float)(sum / (hi - lo));

            // Normalisation sur le maximum recent du registre : un instrument discret et
            // un instrument pousse doivent tous deux se voir.
            _peak[r] = MathF.Max(brut, _peak[r] * 0.9995f);
            _level[r] = Clamp01(brut / MathF.Max(_peak[r], 1e-4f));

            if (sum > 1e-6)
            {
                var bas = MathF.Log2(MathF.Max(lo, 1));
                var etendue = MathF.Log2(MathF.Max(hi - 1, 2)) - bas;
                var pos = etendue > 1e-3f ? (float)(weighted / sum - bas) / etendue : 0.5f;

                // ON NE SUIT QUE CE QU'ON ENTEND. Un centre de gravite calcule sur un
                // registre presque muet saute au gre du bruit de fond, puis saute encore
                // au retour du son. La position d'une note qui n'existe pas n'a pas a
                // etre tenue a jour.
                var vif = 0.04f + 0.16f * _level[r];
                _cible[r] += (Clamp01(pos) - _cible[r]) * vif;
            }

            // Puis un ressort, et non une seconde moyenne : une moyenne exponentielle
            // arrive toujours en retard et sans elan, ce qui fait qu'un mouvement parait
            // mou. Un ressort a une vitesse, donc de l'inertie.
            pitches[r] = _pitch[r].Feed(_cible[r], FrameSeconds);
            levels[r] = _level[r];

            // La montee, et non le niveau : une note tenue ne doit pas declencher en
            // permanence, seule son attaque compte.
            if (_onsets[r].Feed(MathF.Max(0f, _level[r] - _prev[r]))) hits |= 1 << r;
            _prev[r] = _level[r];
        }

        // Les trois agregats restent publies : le paquet GPU et le rendu s'en servent pour
        // ce qui n'a pas besoin du detail — la masse des graves, par exemple.
        var low = (levels[0] + levels[1]) * 0.5f;
        var mid = (levels[2] + levels[3]) * 0.5f;
        var high = (levels[4] + levels[5]) * 0.5f;

        return new Voices(
            low, mid, high,
            (hits & 0b000011) != 0,
            (hits & 0b001100) != 0,
            (hits & 0b110000) != 0,
            (pitches[0] + pitches[1]) * 0.5f,
            (pitches[2] + pitches[3]) * 0.5f,
            (pitches[4] + pitches[5]) * 0.5f,
            levels, pitches, hits);
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
