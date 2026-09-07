using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class FftTests
{
    [Fact]
    public void Une_sinusoide_pure_donne_un_pic_a_sa_frequence()
    {
        // Le controle de base : si la FFT se trompe de bin, toutes les bandes sont
        // decalees et un kick allume les aigus.
        const int n = 1024, bin = 40;
        var re = new float[n];
        var im = new float[n];
        for (var i = 0; i < n; i++)
            re[i] = MathF.Sin(2f * MathF.PI * bin * i / n);

        Fft.Forward(re, im);

        var peak = 0;
        var best = 0f;
        for (var i = 1; i < n / 2; i++)
        {
            var mag = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
            if (mag > best) { best = mag; peak = i; }
        }

        Assert.Equal(bin, peak);
    }

    [Fact]
    public void Une_taille_hors_puissance_de_deux_est_refusee()
    {
        // Un radix 2 sur une taille quelconque ne rend pas une erreur mais un resultat
        // faux : mieux vaut refuser bruyamment.
        Assert.Throws<ArgumentException>(() => Fft.Forward(new float[100], new float[100]));
    }
}

public class OnsetDetectorTests
{
    [Fact]
    public void Un_flux_constant_ne_declenche_jamais()
    {
        // Une nappe soutenue n'est pas une attaque. Un seuil fixe se serait fait avoir.
        var d = new OnsetDetector();
        var fired = 0;
        for (var i = 0; i < 500; i++)
            if (d.Feed(1f)) fired++;

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Une_pointe_franche_declenche_une_seule_fois_avec_un_leger_retard()
    {
        // La decision arrive quelques fenetres apres la pointe : il faut avoir vu la
        // suite pour savoir qu'on etait sur un sommet. Ce retard vaut une soixantaine
        // de millisecondes, sous le seuil de perception d'un decalage son/image.
        var d = new OnsetDetector();
        for (var i = 0; i < 200; i++) d.Feed(1f);      // remplit l'historique

        d.Feed(10f);                                    // la pointe entre dans le tampon

        var fired = 0;
        for (var i = 0; i < 10; i++)                    // le plat qui suit revele le sommet
            if (d.Feed(1f)) fired++;

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Une_montee_progressive_ne_declenche_pas()
    {
        // Un fondu qui monte franchit le seuil sans etre une attaque. Sans la condition
        // de maximum local, il declenchait — et c'est ce qui faisait partir les eclairs
        // n'importe quand.
        var d = new OnsetDetector();
        for (var i = 0; i < 200; i++) d.Feed(1f);

        var fired = 0;
        for (var i = 1; i <= 40; i++)
            if (d.Feed(1f + i * 0.5f)) fired++;         // croissance stricte, jamais de sommet

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Le_seuil_suit_le_morceau()
    {
        // Meme motif, cent fois plus fort : le detecteur doit se comporter pareil.
        // C'est ce qui lui permet de passer d'un ambient feutre a des batteries seches
        // sans reglage.
        static int Count(float scale)
        {
            var d = new OnsetDetector();
            var n = 0;
            for (var i = 0; i < 600; i++)
                if (d.Feed((i % 20 == 0 ? 8f : 1f) * scale)) n++;
            return n;
        }

        Assert.Equal(Count(1f), Count(100f));
    }
}

public class SpectrumAnalyzerTests
{
    [Fact]
    public void Un_grave_pur_allume_les_bandes_basses_pas_les_hautes()
    {
        // Le controle qui compte a l'ecran : un kick ne doit pas faire sauter les aigus.
        var a = new SpectrumAnalyzer(48_000);
        var w = new float[SpectrumAnalyzer.Window];

        VisualFrame f = default;
        for (var pass = 0; pass < 3; pass++)          // laisse le spectre precedent se remplir
        {
            for (var i = 0; i < w.Length; i++)
                w[i] = 0.6f * MathF.Sin(2f * MathF.PI * 60f * i / 48_000f);
            f = a.Analyze(w, pass * 21);
        }

        var low = f.Bands[0] + f.Bands[1];
        var high = f.Bands[^1] + f.Bands[^2];
        Assert.True(low > high, $"grave {low:0.000} devrait depasser aigu {high:0.000}");
    }

    [Fact]
    public void Le_silence_ne_produit_ni_niveau_ni_attaque()
    {
        var a = new SpectrumAnalyzer(48_000);
        var w = new float[SpectrumAnalyzer.Window];

        for (var pass = 0; pass < 60; pass++)
        {
            var f = a.Analyze(w, pass * 21);
            Assert.Equal(0f, f.Rms, 3);
            Assert.False(f.Onset);
        }
    }

    [Fact]
    public void Une_fenetre_de_mauvaise_taille_est_refusee()
    {
        var a = new SpectrumAnalyzer();
        Assert.Throws<ArgumentException>(() => a.Analyze(new float[512], 0));
    }
}
