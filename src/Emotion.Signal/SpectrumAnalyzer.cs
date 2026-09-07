namespace Emotion.Signal;

/// <summary>
/// Transforme une fenetre d'echantillons en une image du signal : niveau, bandes,
/// attaque, tempo.
///
/// Sans etat externe, entierement testable : on lui donne des echantillons, elle rend
/// une <see cref="VisualFrame"/>. Les sources se contentent de la nourrir, qu'elles
/// viennent d'une carte son ou d'un fichier.
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>Taille de fenetre. 1024 a 48 kHz, soit 21 ms : assez court pour qu'un kick reste net.</summary>
    public const int Window = 1024;

    private readonly int _sampleRate;
    private readonly float[] _hann = Fft.Hann(Window);
    private readonly float[] _re = new float[Window];
    private readonly float[] _im = new float[Window];

    private readonly float[] _prevSpectrum = new float[Window / 2];

    // Maximum glissant par bande, pour normaliser sur ce qui joue plutot que sur une
    // constante. Sans lui, un morceau fort sature les douze bandes a 1 et le visuel
    // n'a plus aucun relief, tandis qu'un morceau feutre ne fait rien bouger.
    private readonly float[] _bandPeak = new float[VisualFrame.BandCount];
    private readonly OnsetDetector _onsets = new();

    // Un detecteur par registre. Ils partagent la mecanique — flux positif, seuil
    // adaptatif, ecart minimal — mais chacun ne regarde que sa tranche de spectre,
    // et chacun se cale donc sur le niveau de bruit qui lui est propre.
    private readonly OnsetDetector _kick = new();
    private readonly OnsetDetector _clap = new();
    private readonly OnsetDetector _hat = new(minGap: 4);   // les charleys vont vite
    private readonly float[] _prevBand = new float[VisualFrame.BandCount];
    private readonly TempoEstimator _tempo = new();

    // Les bandes suivent une echelle logarithmique : l'oreille entend le rapport entre
    // deux frequences, pas leur difference. Douze bandes lineaires donneraient onze
    // bandes d'aigus et une seule pour tout le grave.
    private readonly int[] _edges;

    public SpectrumAnalyzer(int sampleRate = 48_000)
    {
        _sampleRate = sampleRate;
        _edges = BuildEdges(sampleRate);
    }

    /// <summary>Tempo estime, nul tant que la detection n'a pas accroche.</summary>
    public float? Bpm => _tempo.Bpm;

    /// <summary>
    /// Analyse une fenetre. <paramref name="samples"/> doit contenir
    /// <see cref="Window"/> echantillons mono dans [-1, 1].
    /// </summary>
    public VisualFrame Analyze(ReadOnlySpan<float> samples, long tMs)
    {
        if (samples.Length != Window)
            throw new ArgumentException($"fenetre de {Window} echantillons attendue", nameof(samples));

        var sum = 0f;
        for (var i = 0; i < Window; i++)
        {
            var s = samples[i];
            sum += s * s;
            _re[i] = s * _hann[i];
            _im[i] = 0f;
        }

        // Racine de la moyenne des carres, puis compression : un signal musical vit
        // dans le bas de l'echelle lineaire, et un visuel qui suit le RMS brut reste
        // ecrase en permanence.
        var rms = MathF.Sqrt(sum / Window);
        var level = Clamp01(MathF.Pow(rms * 3.2f, 0.55f));

        Fft.Forward(_re, _im);

        var half = Window / 2;
        Span<float> spectrum = stackalloc float[half];
        for (var i = 0; i < half; i++)
            spectrum[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

        // Flux spectral positif : on ne compte que ce qui monte. Une note qui s'eteint
        // n'est pas une attaque.
        //
        // Il est calcule sur le seul registre du kick, pas sur tout le spectre, et
        // c'est un choix dicte par le repertoire. Le barber beats est plein de souffle,
        // de crepitement de vinyle et de nappes qui bougent : ce bruit remplit les
        // aigus de flux en permanence et noie la seule montee qui compte. Mesure sur
        // instamata : en pleine bande, le detecteur voyait quatre attaques par temps.
        var kickBins = Math.Min(half, _edges[KickBandLimit]);

        var flux = 0f;
        for (var i = 0; i < half; i++)
        {
            var d = spectrum[i] - _prevSpectrum[i];
            if (d > 0 && i < kickBins) flux += d;
            _prevSpectrum[i] = spectrum[i];
        }

        var onset = _onsets.Feed(flux);
        if (onset) _tempo.Mark(tMs);

        var bands = new float[VisualFrame.BandCount];
        for (var b = 0; b < bands.Length; b++)
        {
            var lo = _edges[b];
            var hi = _edges[b + 1];
            var peak = 0f;
            for (var i = lo; i < hi && i < half; i++)
                if (spectrum[i] > peak) peak = spectrum[i];

            // Le pic plutot que la moyenne : sur une bande large, une moyenne noie une
            // pointe unique, or c'est justement la pointe qui se voit a l'ecran.
            //
            // Puis normalisation sur le maximum recent de cette bande, qui redescend
            // lentement. C'est un controle de gain : le visuel garde son relief que le
            // morceau soit pousse ou feutre, sans que Selim ait a toucher a un niveau.
            _bandPeak[b] = MathF.Max(peak, _bandPeak[b] * PeakDecay);
            var reference = MathF.Max(_bandPeak[b], MinReference);
            bands[b] = Clamp01(MathF.Pow(peak / reference, 0.7f));
        }

        // Flux par registre, calcule sur les bandes deja normalisees : chaque detecteur
        // se cale ainsi sur le contraste de sa tranche et non sur son volume absolu,
        // ce qui evite qu'un mix charge en graves eteigne la detection des claps.
        var hits = new Hits(
            Kick: _kick.Feed(BandRise(bands, 0, 3)),
            Clap: _clap.Feed(BandRise(bands, 3, 8)),
            Hat:  _hat.Feed(BandRise(bands, 9, VisualFrame.BandCount)));

        Array.Copy(bands, _prevBand, bands.Length);

        // Flux et seuil sont ramenes sur une echelle commune : ce qui compte a l'ecran
        // de diagnostic est leur rapport, pas leur valeur absolue.
        var scale = MathF.Max(_onsets.Threshold * 2f, 1e-6f);

        return new VisualFrame(
            tMs, level, bands, onset, _tempo.Phase(tMs), _tempo.Bpm,
            Hits: hits,
            Flux: Clamp01(flux / scale),
            Threshold: Clamp01(_onsets.Threshold / scale));
    }

    /// <summary>
    /// Bornes des bandes, de 30 Hz a 16 kHz, reparties geometriquement.
    /// </summary>
    private static int[] BuildEdges(int sampleRate)
    {
        const float lowHz = 30f, highHz = 16_000f;
        var n = VisualFrame.BandCount;
        var edges = new int[n + 1];
        var binHz = sampleRate / (float)Window;

        for (var i = 0; i <= n; i++)
        {
            var hz = lowHz * MathF.Pow(highHz / lowHz, i / (float)n);
            edges[i] = Math.Max(1, (int)(hz / binHz));
        }

        // Deux bornes egales donneraient une bande vide : on force la croissance.
        for (var i = 1; i <= n; i++)
            if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 1;

        return edges;
    }

    /// <summary>Montee d'energie sur une tranche de bandes, depuis la fenetre precedente.</summary>
    private float BandRise(float[] bands, int from, int to)
    {
        var rise = 0f;
        for (var i = from; i < to && i < bands.Length; i++)
        {
            var d = bands[i] - _prevBand[i];
            if (d > 0) rise += d;
        }
        return rise;
    }

    /// <summary>
    /// Jusqu'ou monte le registre pris en compte pour les attaques : les cinq premieres
    /// bandes, soit environ 30 a 400 Hz. Le kick et le bas de la caisse claire.
    /// </summary>
    private const int KickBandLimit = 5;

    /// <summary>
    /// Vitesse a laquelle le maximum d'une bande redescend, par fenetre. A 0,999 sur
    /// 47 fenetres par seconde, il faut une quinzaine de secondes pour oublier un pic :
    /// assez lent pour ne pas pomper au rythme de la musique, assez rapide pour suivre
    /// un changement de morceau.
    /// </summary>
    private const float PeakDecay = 0.999f;

    /// <summary>Plancher, pour que le silence ne soit pas amplifie en bruit plein ecran.</summary>
    private const float MinReference = 4f;

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
