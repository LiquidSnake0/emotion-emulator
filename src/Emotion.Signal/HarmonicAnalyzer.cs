namespace Emotion.Signal;

/// <summary>
/// Analyse harmonique : ce qui sonne, par opposition a ce qui frappe.
///
/// <b>Une fenetre plus longue que celle des percussions, et c'est le point cle.</b>
/// Les deux analyses ont des besoins opposes : une attaque demande une fenetre courte
/// pour rester nette dans le temps, une note demande une fenetre longue pour etre
/// precise en frequence. On ne peut pas avoir les deux a la fois — c'est la limite de
/// Gabor, pas un defaut d'implementation.
///
/// A 1024 points et 48 kHz, un bin vaut 47 Hz : le do central est a 261 Hz, le re a
/// 294, ils tombent dans des bins voisins et tout l'aigu du piano s'ecrase. A 4096
/// points, un bin vaut 11,7 Hz, ce qui separe proprement les demi-tons au-dessus de
/// 200 Hz — la ou vit le piano d'instamata.
///
/// La contrepartie est une resolution temporelle de 85 ms, largement suffisante : un
/// changement d'accord n'a pas besoin d'etre date a la milliseconde, contrairement a
/// un kick.
/// </summary>
public sealed class HarmonicAnalyzer
{
    /// <summary>Taille de fenetre. 4096 a 48 kHz : 11,7 Hz par bin, 85 ms de fenetre.</summary>
    public const int Window = 4096;

    /// <summary>
    /// Bornes de l'analyse en hauteurs. En dessous de 180 Hz la resolution ne separe
    /// plus les demi-tons ; au-dessus de 2,5 kHz on ne trouve que des harmoniques, qui
    /// brouillent le profil plus qu'elles ne l'enrichissent.
    /// </summary>
    private const float LowHz = 180f;
    private const float HighHz = 2500f;

    private readonly int _sampleRate;
    private readonly float[] _hann = Fft.Hann(Window);
    private readonly float[] _re = new float[Window];
    private readonly float[] _im = new float[Window];
    private readonly float[] _ring = new float[Window];
    private int _write;
    private int _since;

    private readonly int[] _classOf;              // bin -> classe de hauteur, -1 si hors bornes
    private readonly float[] _chroma = new float[Harmony.Classes];
    private readonly float[] _previous = new float[Harmony.Classes];
    private bool _hasPrevious;

    private Harmony _last = Harmony.None;

    public HarmonicAnalyzer(int sampleRate = 48_000)
    {
        _sampleRate = sampleRate;
        _classOf = BuildClassMap(sampleRate);
    }

    /// <summary>
    /// Accumule des echantillons et rend l'analyse courante. Elle n'est recalculee
    /// qu'une fois par saut ; entre deux, la derniere valeur est repetee, ce qui evite
    /// de faire une FFT de 4096 points a chaque fenetre de percussion.
    /// </summary>
    public Harmony Feed(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _ring[_write] = s;
            _write = (_write + 1) % Window;
        }

        _since += samples.Length;
        if (_since < Window / 4) return _last;     // saut de 1024, soit 21 ms
        _since = 0;

        return _last = Compute();
    }

    private Harmony Compute()
    {
        // Le tampon circulaire est remis a plat, du plus ancien au plus recent.
        for (var i = 0; i < Window; i++)
        {
            var s = _ring[(_write + i) % Window];
            _re[i] = s * _hann[i];
            _im[i] = 0f;
        }

        Fft.Forward(_re, _im);

        var half = Window / 2;
        Array.Clear(_chroma);

        // Platitude spectrale : moyenne geometrique sur moyenne arithmetique. Proche de
        // 1 le spectre est plat, donc bruite ; proche de 0 il est concentre sur des
        // raies, donc tonal. On la calcule en logarithmes pour ne pas perdre la moyenne
        // geometrique dans un depassement de capacite.
        var logSum = 0.0;
        var linSum = 0.0;
        var counted = 0;

        for (var i = 1; i < half; i++)
        {
            var mag = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

            var c = _classOf[i];
            if (c >= 0) _chroma[c] += mag;

            if (i >= 4 && i < half / 2)
            {
                logSum += Math.Log(mag + 1e-9);
                linSum += mag;
                counted++;
            }
        }

        var flatness = 0f;
        if (counted > 0 && linSum > 1e-6)
        {
            var geo = Math.Exp(logSum / counted);
            var ari = linSum / counted;
            flatness = (float)(geo / ari);
        }
        var tonality = Clamp01(1f - flatness * 2.2f);

        // Normalisation du profil : c'est la forme qui compte, pas le volume. Un accord
        // doit donner le meme profil joue fort ou joue doux.
        var max = 0f;
        foreach (var v in _chroma) if (v > max) max = v;
        if (max > 1e-6f)
            for (var i = 0; i < _chroma.Length; i++) _chroma[i] /= max;
        else
            Array.Clear(_chroma);

        // Dominante et nettete : l'ecart entre la premiere et la deuxieme classe. Une
        // note tenue detache franchement, un accord dense beaucoup moins.
        int? pitch = null;
        var first = 0f; var second = 0f;
        for (var i = 0; i < _chroma.Length; i++)
        {
            if (_chroma[i] > first) { second = first; first = _chroma[i]; pitch = i; }
            else if (_chroma[i] > second) second = _chroma[i];
        }
        var strength = first > 1e-6f ? Clamp01(first - second) : 0f;
        if (max <= 1e-6f) pitch = null;

        // Changement : distance entre profils successifs, ramenee sur 0 a 1. C'est le
        // pendant harmonique d'une attaque — un changement d'accord, pas une frappe.
        var change = 0f;
        if (_hasPrevious)
        {
            var d = 0f;
            for (var i = 0; i < _chroma.Length; i++)
            {
                var diff = _chroma[i] - _previous[i];
                d += diff * diff;
            }
            change = Clamp01(MathF.Sqrt(d / _chroma.Length) * 2.5f);
        }

        Array.Copy(_chroma, _previous, _chroma.Length);
        _hasPrevious = true;

        return new Harmony((float[])_chroma.Clone(), pitch, strength, change, tonality);
    }

    /// <summary>
    /// Associe chaque bin a une classe de hauteur, ou -1 hors des bornes utiles.
    /// Precalcule une fois : le faire a chaque fenetre couterait un logarithme par bin,
    /// quarante-sept fois par seconde.
    /// </summary>
    private int[] BuildClassMap(int sampleRate)
    {
        var half = Window / 2;
        var map = new int[half];
        var binHz = sampleRate / (float)Window;

        for (var i = 0; i < half; i++)
        {
            var hz = i * binHz;
            if (hz < LowHz || hz > HighHz) { map[i] = -1; continue; }

            // Numero de note MIDI, puis repliement des octaves. 69 est le la 440.
            var midi = 69f + 12f * MathF.Log2(hz / 440f);
            var cls = ((int)MathF.Round(midi) % 12 + 12) % 12;
            map[i] = cls;
        }
        return map;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
