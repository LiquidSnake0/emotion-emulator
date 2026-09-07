namespace Emotion.Signal;

/// <summary>
/// Fabrique un signal plausible a partir d'un tempo, sans aucune carte son.
///
/// Ce n'est pas un bouche-trou : c'est ce qui permet de construire et de regler tout
/// le visuel avant d'avoir la table branchee, et de rejouer deux fois exactement la
/// meme sequence quand on compare deux versions d'un shader. Les valeurs sont
/// deterministes pour une graine donnee.
///
/// Ce qu'elle imite du barber beats : un kick sur chaque temps, des charleys sur les
/// contretemps, des nappes qui bougent lentement dans les mediums.
/// </summary>
public sealed class MockAudioSource : IAudioSource
{
    private readonly float _bpm;
    private readonly int _seed;
    private readonly TimeSpan _tick;

    /// <param name="bpm">Tempo impose. Le crate vit entre 82 et 97.</param>
    /// <param name="fps">Images par seconde. 60 pour coller au rafraichissement du renderer.</param>
    /// <param name="seed">Graine des nappes, pour rejouer la meme sequence.</param>
    public MockAudioSource(float bpm = 87f, int fps = 60, int seed = 1203)
    {
        if (bpm <= 0) throw new ArgumentOutOfRangeException(nameof(bpm));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        _bpm = bpm;
        _seed = seed;
        _tick = TimeSpan.FromSeconds(1.0 / fps);
    }

    public string Name => $"mock {_bpm:0.#} BPM";

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        long lastBeatIndex = -1;

        while (!ct.IsCancellationRequested)
        {
            var t = (long)(DateTime.UtcNow - start).TotalMilliseconds;
            var frame = At(t, ref lastBeatIndex);
            yield return frame;

            try { await Task.Delay(_tick, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// <summary>
    /// Le signal a un instant donne. Extrait de la boucle pour que les tests
    /// interrogent n'importe quel instant sans attendre l'horloge.
    /// </summary>
    /// <param name="lastBeatIndex">
    /// Dernier temps deja annonce. <see cref="VisualFrame.Beat"/> est une impulsion :
    /// il ne doit etre vrai que sur la premiere image apres l'attaque, sinon le
    /// renderer declencherait son effet a chaque image de la duree du temps.
    /// </param>
    public VisualFrame At(long t, ref long lastBeatIndex)
    {
        var beatMs = 60_000.0 / _bpm;
        var beats = t / beatMs;              // temps ecoules, partie decimale = position dans le temps
        var beatIndex = (long)Math.Floor(beats);
        var inBeat = (float)(beats - beatIndex);

        var beat = beatIndex > lastBeatIndex;
        if (beat) lastBeatIndex = beatIndex;

        // Kick : attaque nette, retombee rapide. Porte les trois bandes graves.
        var kick = Decay(inBeat, 8f);

        // Charley sur les contretemps, plus court et plus discret.
        var offbeat = (inBeat + 0.5f) % 1f;
        var hat = Decay(offbeat, 26f) * 0.55f;

        var bands = new float[VisualFrame.BandCount];
        for (var i = 0; i < bands.Length; i++)
        {
            var x = i / (float)(bands.Length - 1);          // 0 grave, 1 aigu
            var pad = Pad(t, i);                            // nappe lente, propre a la bande
            var low = kick * Falloff(x, 0f, 0.30f);         // le kick n'existe que dans le bas
            var high = hat * Falloff(x, 1f, 0.35f);         // le charley que dans le haut
            var mid = pad * Falloff(x, 0.45f, 0.55f) * 0.7f;

            bands[i] = Clamp01(low + high + mid);
        }

        // Le niveau global suit surtout le kick : c'est ce qu'on entend.
        var rms = Clamp01(0.25f + 0.55f * kick + 0.20f * Average(bands));

        // Phase sur quatre temps : de quoi animer une figure a l'echelle de la mesure.
        var phase = (float)(((beats % 4) + 4) % 4 / 4.0);

        return new VisualFrame(t, rms, bands, beat, phase, _bpm);
    }

    /// <summary>Enveloppe percussive : 1 a l'attaque, decroissance exponentielle.</summary>
    private static float Decay(float x, float rate) => MathF.Exp(-rate * x);

    /// <summary>Cloche centree sur <paramref name="center"/>, pour repartir une source sur les bandes.</summary>
    private static float Falloff(float x, float center, float width)
    {
        var d = (x - center) / width;
        return MathF.Exp(-d * d);
    }

    /// <summary>
    /// Nappe : somme de trois sinus incommensurables, donc jamais tout a fait la meme
    /// forme deux fois, mais entierement determinee par t et par la graine.
    /// </summary>
    private float Pad(long t, int band)
    {
        var s = (_seed + band * 37) * 0.001f;
        var sec = t / 1000f;
        var v = MathF.Sin(sec * 0.31f + s)
              + MathF.Sin(sec * 0.53f + s * 2.1f)
              + MathF.Sin(sec * 0.11f + s * 4.7f);
        return (v / 3f + 1f) / 2f;   // ramene de [-1,1] a [0,1]
    }

    private static float Average(float[] xs)
    {
        var sum = 0f;
        foreach (var x in xs) sum += x;
        return xs.Length == 0 ? 0f : sum / xs.Length;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
