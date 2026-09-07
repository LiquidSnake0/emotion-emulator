namespace Emotion.Signal;

/// <summary>
/// Ce qui joue des notes, reparti en trois registres.
///
/// Les <see cref="Hits"/> couvrent ce qui frappe. Mais un morceau comme instamata est
/// plein d'instruments qui ne frappent pas : un piano, un xylophone, une voix, une
/// nappe. Un detecteur d'attaque ne les voit pas, et un chromagramme global les melange
/// tous en un seul profil de hauteurs — on sait alors <i>quelle note</i> sonne, jamais
/// <b>qui</b> la joue.
///
/// D'ou ce decoupage. On ne cherche pas a nommer les instruments, ce qui demanderait un
/// modele entraine et resterait fragile ; on les separe par le registre qu'ils occupent,
/// ce qui suffit a leur donner des gestes distincts a l'ecran :
///
/// <code>
///   Low    100 - 350 Hz    basse, main gauche du piano, cordes graves
///   Mid    350 - 1500 Hz   voix, corps du piano, cuivres
///   High   1500 - 6000 Hz  xylophone, cloches, harmoniques brillantes
/// </code>
///
/// Ces trois canaux travaillent sur la <b>composante harmonique</b> issue de la
/// separation, pas sur le signal brut. C'est ce qui les rend possibles : sur le signal
/// entier, chaque coup de caisse claire ferait bondir les trois registres a la fois.
/// La separation etait deja calculee et sa moitie harmonique n'etait pas utilisee.
/// </summary>
/// <param name="Low">Niveau tonal du grave, 0 a 1.</param>
/// <param name="Mid">Niveau tonal du medium, 0 a 1.</param>
/// <param name="High">Niveau tonal de l'aigu, 0 a 1.</param>
/// <param name="LowHit">Une note vient d'etre jouee dans le grave.</param>
/// <param name="MidHit">Une note vient d'etre jouee dans le medium.</param>
/// <param name="HighHit">Une note vient d'etre jouee dans l'aigu.</param>
public readonly record struct Voices(
    float Low, float Mid, float High,
    bool LowHit, bool MidHit, bool HighHit)
{
    public static readonly Voices None = default;

    /// <summary>Une note a ete jouee, quel que soit le registre.</summary>
    public bool Any => LowHit || MidHit || HighHit;
}

/// <summary>
/// Suit les trois registres tonals et signale les notes jouees.
///
/// Une note de piano n'a pas l'attaque franche d'une caisse claire : elle monte plus
/// doucement et retombe lentement. Les detecteurs de percussion, regles pour des
/// sommets nets et espaces de 426 ms au minimum, la manqueraient. Ceux-ci sont donc
/// plus permissifs sur la forme et plus rapproches dans le temps — une main gauche de
/// piano joue volontiers deux notes par temps.
/// </summary>
public sealed class VoiceTracker
{
    private readonly int[] _edges;             // bornes des trois registres, en bins
    private readonly float[] _level = new float[3];
    private readonly float[] _prev = new float[3];
    private readonly float[] _peak = [1e-3f, 1e-3f, 1e-3f];

    private readonly OnsetDetector[] _onsets =
    [
        // Huit fenetres, soit 170 ms : deux notes par temps restent distinctes a
        // 87 BPM, la ou la limite des percussions en autoriserait une seule.
        new OnsetDetector(minGap: 8),
        new OnsetDetector(minGap: 8),
        new OnsetDetector(minGap: 8),
    ];

    public VoiceTracker(int sampleRate, int window)
    {
        var binHz = sampleRate / (float)window;
        _edges =
        [
            Math.Max(1, (int)(100f / binHz)),
            Math.Max(2, (int)(350f / binHz)),
            Math.Max(3, (int)(1500f / binHz)),
            Math.Max(4, (int)(6000f / binHz)),
        ];
    }

    /// <summary>
    /// Analyse la composante harmonique d'une fenetre.
    /// </summary>
    public Voices Feed(ReadOnlySpan<float> harmonic)
    {
        Span<float> raw = stackalloc float[3];

        for (var r = 0; r < 3; r++)
        {
            var sum = 0f;
            var lo = _edges[r];
            var hi = Math.Min(_edges[r + 1], harmonic.Length);
            for (var i = lo; i < hi; i++) sum += harmonic[i];
            raw[r] = hi > lo ? sum / (hi - lo) : 0f;

            // Normalisation sur le maximum recent du registre, comme pour les bandes :
            // un piano discret et un piano pousse doivent tous deux se voir.
            _peak[r] = MathF.Max(raw[r], _peak[r] * 0.9995f);
            _level[r] = Clamp01(raw[r] / MathF.Max(_peak[r], 1e-4f));
        }

        // La montee, et non le niveau : une note tenue ne doit pas declencher en
        // permanence, seule son attaque compte.
        var lowRise = MathF.Max(0f, _level[0] - _prev[0]);
        var midRise = MathF.Max(0f, _level[1] - _prev[1]);
        var highRise = MathF.Max(0f, _level[2] - _prev[2]);

        for (var r = 0; r < 3; r++) _prev[r] = _level[r];

        return new Voices(
            _level[0], _level[1], _level[2],
            _onsets[0].Feed(lowRise),
            _onsets[1].Feed(midRise),
            _onsets[2].Feed(highRise));
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
