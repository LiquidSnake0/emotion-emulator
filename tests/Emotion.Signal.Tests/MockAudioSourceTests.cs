using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class MockAudioSourceTests
{
    private static VisualFrame FrameAt(MockAudioSource src, long t)
    {
        long last = -1;
        return src.At(t, ref last);
    }

    [Fact]
    public void Les_bandes_restent_dans_zero_un()
    {
        // Le shader lit ces valeurs sans les borner. Une bande au-dela de 1 se
        // traduirait par un blanc brule a l'ecran, une valeur negative par un trou.
        var src = new MockAudioSource(bpm: 87f);

        for (long t = 0; t < 20_000; t += 7)
        {
            var f = FrameAt(src, t);
            Assert.Equal(VisualFrame.BandCount, f.Bands.Length);
            Assert.All(f.Bands, b => Assert.InRange(b, 0f, 1f));
            Assert.InRange(f.Rms, 0f, 1f);
            Assert.NotNull(f.Phase);
        Assert.InRange(f.Phase!.Value, 0f, 1f);
        }
    }

    [Fact]
    public void Le_beat_est_une_impulsion_pas_un_etat()
    {
        // Si Beat restait vrai pendant toute la duree du temps, le renderer
        // redeclencherait son effet a chaque image et l'ecran resterait fige au maximum.
        var src = new MockAudioSource(bpm: 120f);   // 500 ms par temps
        long last = -1;

        var beats = 0;
        for (long t = 0; t < 4_000; t += 10)        // 4 s, donc huit temps
            if (src.At(t, ref last).Onset) beats++;

        Assert.Equal(8, beats);
    }

    [Fact]
    public void Le_kick_frappe_dans_les_graves_pas_dans_les_aigus()
    {
        // Un visuel qui fait sauter les aigus sur le kick sonne faux a l'oeil.
        var src = new MockAudioSource(bpm: 120f);

        var onBeat = FrameAt(src, 0);               // pile sur l'attaque
        var grave = onBeat.Bands[0];
        var aigu = onBeat.Bands[^1];

        Assert.True(grave > aigu * 2f,
            $"le grave ({grave:0.000}) devrait dominer l'aigu ({aigu:0.000}) sur l'attaque");
    }

    [Fact]
    public void La_phase_boucle_sur_quatre_temps()
    {
        // La mesure sert a animer plus lentement que le beat : elle doit repasser
        // par zero tous les quatre temps, pas tous les temps.
        var src = new MockAudioSource(bpm: 120f);   // 500 ms par temps, 2 s la mesure

        Assert.Equal(0.00f, FrameAt(src, 0).Phase!.Value, 2);
        Assert.Equal(0.25f, FrameAt(src, 500).Phase!.Value, 2);
        Assert.Equal(0.75f, FrameAt(src, 1500).Phase!.Value, 2);
        Assert.Equal(0.00f, FrameAt(src, 2000).Phase!.Value, 2);
    }

    [Fact]
    public void Deux_sources_de_meme_graine_donnent_le_meme_signal()
    {
        // C'est ce qui permet de comparer deux versions d'un shader sur exactement
        // la meme sequence, au lieu de juger a l'oeil sur deux passages differents.
        var a = new MockAudioSource(bpm: 90f, seed: 42);
        var b = new MockAudioSource(bpm: 90f, seed: 42);

        for (long t = 0; t < 5_000; t += 33)
            Assert.Equal(FrameAt(a, t).Bands, FrameAt(b, t).Bands);
    }

    [Fact]
    public async Task Le_flux_s_arrete_a_l_annulation()
    {
        // Sans quoi l'arret du service pendrait indefiniment.
        var src = new MockAudioSource(bpm: 87f, fps: 200);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        var seen = 0;
        var run = Task.Run(async () =>
        {
            await foreach (var _ in src.ReadAsync(cts.Token)) seen++;
        });

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished);
        Assert.True(seen > 0, "aucune image n'a ete emise");
    }
}
