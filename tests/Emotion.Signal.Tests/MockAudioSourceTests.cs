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
    public void Le_mock_declenche_les_trois_registres()
    {
        // Le mock existe pour regler le visuel sans materiel. S'il n'alimente pas les
        // registres, aucun effet ne part et il ne simule plus rien : un signal fabrique
        // doit remplir le meme contrat qu'un vrai signal.
        var src = new MockAudioSource(bpm: 120f);   // 500 ms par temps
        long last = -1;

        int kick = 0, clap = 0, hat = 0;
        for (long t = 0; t < 4_000; t += 5)         // 4 s, huit temps, deux mesures
        {
            var f = src.At(t, ref last);
            if (f.Hits.Kick) kick++;
            if (f.Hits.Clap) clap++;
            if (f.Hits.Hat) hat++;
        }

        Assert.Equal(8, kick);                       // un par temps
        Assert.Equal(4, clap);                       // le deux et le quatre de chaque mesure
        Assert.Equal(8, hat);                        // un par contretemps
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

    [Fact]
    public void Le_mock_remplit_tout_le_contrat_d_un_vrai_signal()
    {
        // LE PIEGE QUI S'EST DEJA REFERME DEUX FOIS. Le mock existe pour regler le visuel
        // sans platines ni table. Chaque fois que le contrat s'etend — les frappes hier,
        // les registres, le timbre et la structure aujourd'hui — il cesse silencieusement
        // de le remplir, et l'ecran reste eteint en mode simule sans qu'aucun test ne
        // proteste. Ce test verifie qu'aucun champ du contrat n'est reste a sa valeur par
        // defaut sur une seconde entiere.
        var mock = new MockAudioSource(bpm: 88f);
        long last = -1;

        var sawVoice = false; var sawTimbre = false; var sawStructure = false;
        var sawBar = false; var sawHit = false; var sawGesture = false;

        for (long t = 0; t < 30_000; t += 21)
        {
            var f = mock.At(t, ref last);
            if (f.Voices.Mid > 0f || f.Voices.Low > 0f) sawVoice = true;
            if (f.Timbre.Openness > 0f) sawTimbre = true;
            if (f.Structure.Confidence > 0f) sawStructure = true;
            if (f.Structure.BarStart) sawBar = true;
            if (f.Hits.Any) sawHit = true;
            if (f.Gestures.FilterClosed || f.Gestures.Dense) sawGesture = true;
        }

        Assert.True(sawHit, "aucune frappe");
        Assert.True(sawVoice, "aucun registre tonal");
        Assert.True(sawTimbre, "aucune couleur de son");
        Assert.True(sawStructure, "aucune structure");
        Assert.True(sawBar, "aucun debut de mesure");
        Assert.True(sawGesture, "aucun geste");
    }
}
