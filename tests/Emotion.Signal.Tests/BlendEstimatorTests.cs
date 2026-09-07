using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class BlendEstimatorTests
{
    private const int N = VisualFrame.BandCount;

    /// <summary>
    /// Un profil de bandes qui evolue, pour tenir lieu de morceau. Deux graines
    /// differentes donnent des <b>dynamiques</b> differentes, pas seulement des phases :
    /// c'est ce qui distingue reellement deux disques, deux morceaux ayant de toute
    /// facon la meme allure spectrale generale.
    /// </summary>
    private static float[] Track(int seed, int step)
    {
        var rate = seed == 1 ? 0.13f : 0.31f;      // tempos distincts
        var b = new float[N];
        for (var i = 0; i < N; i++)
            b[i] = 0.5f + 0.45f * MathF.Sin(step * rate + i * 0.7f + seed * 3.1f);
        return b;
    }

    /// <summary>Melange deux profils : c'est ce que fait le fader.</summary>
    private static float[] Mix(float[] a, float[] b, float part)
    {
        var m = new float[N];
        for (var i = 0; i < N; i++) m[i] = a[i] * (1 - part) + b[i] * part;
        return m;
    }

    private static float Settle(BlendEstimator e, Func<int, (float[] master, float[] cue)> feed,
                                int steps = 900)
    {
        var v = 0f;
        for (var s = 0; s < steps; s++)
        {
            var (m, c) = feed(s);
            v = e.Feed(m, c);
        }
        return v;
    }

    [Fact]
    public void Fader_ferme_le_prepare_n_est_pas_dans_le_master()
    {
        // Le cas de depart d'une transition : B tourne au casque, le public n'entend
        // que A. Le mur ne doit pas bouger d'un pixel.
        var e = new BlendEstimator();
        var v = Settle(e, s => (Track(1, s), Track(9, s)));

        Assert.InRange(v, 0f, 0.25f);
    }

    [Fact]
    public void Fader_ouvert_le_master_est_le_prepare()
    {
        // Fin de transition : A est coupe, le master est B. Le mur doit avoir fini de
        // basculer.
        var e = new BlendEstimator();
        var v = Settle(e, s => (Track(9, s), Track(9, s)));

        Assert.InRange(v, 0.75f, 1f);
    }

    [Fact]
    public void A_mi_course_le_melange_se_lit_entre_les_deux()
    {
        // La relation n'est pas lineaire en fonction du fader, et il ne faut pas
        // chercher a la rendre lineaire : a mi-course, mesure autour de 0,8, le nouveau
        // morceau domine deja la perception. Ce que le visuel doit suivre est l'effet
        // sur l'oreille, pas la position mecanique du potentiometre.
        var e = new BlendEstimator();
        var v = Settle(e, s => (Mix(Track(1, s), Track(9, s), 0.5f), Track(9, s)));

        Assert.InRange(v, 0.2f, 0.95f);
    }

    [Fact]
    public void La_mesure_croit_avec_le_fader()
    {
        // La propriete qui compte vraiment : le visuel doit suivre le geste, donc la
        // mesure doit etre monotone. Peu importe sa valeur exacte a mi-course.
        static float At(float part)
        {
            var e = new BlendEstimator();
            return Settle(e, s => (Mix(Track(1, s), Track(9, s), part), Track(9, s)));
        }

        var v0 = At(0f);
        var v50 = At(0.5f);
        var v100 = At(1f);

        Assert.True(v0 < v50, $"fader ferme {v0:0.00} devrait etre sous mi-course {v50:0.00}");
        Assert.True(v50 < v100, $"mi-course {v50:0.00} devrait etre sous ouvert {v100:0.00}");
    }

    [Fact]
    public void La_mesure_ignore_le_niveau_d_ecoute()
    {
        // Pearson et non un produit scalaire : un cue ecoute fort au casque et le meme
        // morceau discret dans le master doivent donner la meme mesure. Sinon on
        // mesurerait le volume et non la presence.
        static float At(float cueGain)
        {
            var e = new BlendEstimator();
            return Settle(e, s =>
            {
                var b = Track(9, s);
                var loud = new float[N];
                for (var i = 0; i < N; i++) loud[i] = Math.Min(1f, b[i] * cueGain);
                return (b, loud);
            });
        }

        Assert.Equal(At(1f), At(0.4f), 1);
    }

    [Fact]
    public void Une_remise_a_zero_efface_l_historique()
    {
        // Nouveau disque au casque : ce qui precede n'a plus de sens.
        var e = new BlendEstimator();
        Settle(e, s => (Track(9, s), Track(9, s)));
        Assert.True(e.Blend > 0.5f);

        e.Reset();
        Assert.Equal(0f, e.Blend);
    }
}
