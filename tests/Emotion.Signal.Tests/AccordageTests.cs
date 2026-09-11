using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// L'accordage du disque se releve au cue et decale l'axe des gabarits : sur un vinyle
/// ralenti d'un quart de ton, chaque note tombait pile entre deux cases.
/// </summary>
public class AccordageTests
{
    private const int Rate = 16_000;
    private const int Hop = 512;

    private static SourceSeparator Jouer(float cents, int images = 400)
    {
        var sep = new SourceSeparator(Rate, Hop, 300) { ApprentissageEnLigne = true };
        var bloc = new float[Hop];
        var phases = new double[6];
        // Six notes justes de la gamme (do, re, mi, sol, la, do), toutes desaccordees d'autant.
        float[] hz = [130.81f, 146.83f, 164.81f, 196.00f, 220.00f, 261.63f];
        for (var t = 0; t < images; t++)
        {
            Array.Clear(bloc);
            for (var k = 0; k < hz.Length; k++)
            {
                var f = hz[k] * MathF.Pow(2f, cents / 1200f);
                var pas = 2 * Math.PI * f / Rate;
                var gain = (t / 40 + k) % 3 == 0 ? 0.2f : 0.02f;
                for (var j = 0; j < Hop; j++) { bloc[j] += gain * (float)Math.Sin(phases[k]); phases[k] += pas; }
            }
            sep.Feed(bloc);
        }
        return sep;
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(40f)]
    [InlineData(-45f)]
    public void L_accordage_du_disque_est_retrouve(float cents)
    {
        var sep = Jouer(cents);
        var attendu = MathF.Abs(cents) < 15f ? 0f : cents;
        Assert.True(MathF.Abs(sep.AccordageCents - attendu) <= 7.5f,
            $"desaccorde de {cents} cents, le moteur a releve {sep.AccordageCents} (attendu {attendu} a une demi-case pres)");
    }

    [Fact]
    public void Un_nouveau_disque_repart_de_l_axe_standard()
    {
        var sep = Jouer(40f);
        Assert.True(MathF.Abs(sep.AccordageCents - 40f) <= 7.5f);
        sep.Reset();
        Assert.Equal(0f, sep.AccordageCents);
        Assert.Equal(ProfileLearner.F0, sep.F0);
    }
}
