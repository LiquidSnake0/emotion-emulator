using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>Le caractere d'une source se lit sur la duree : ce qui frappe, ce qui tient.</summary>
public class SourceEnvelopeCaractereTests
{
    private const float Image = 1024f / 48_000f;
    private const int VingtSecondes = (int)(20f / Image);

    [Fact]
    public void Une_source_qui_frappe_a_un_caractere_bas()
    {
        var env = new SourceEnvelope(1, Image);
        // Un coup toutes les 24 images (une demi-seconde), qui meurt en trois images.
        for (var i = 0; i < 2 * VingtSecondes; i++)
        {
            var depuis = i % 24;
            env.Feed(0, depuis == 0 ? 1f : depuis < 3 ? 0.5f / depuis : 0.02f);
        }
        Assert.True(env.Caractere(0) < 0.45f, $"un train de coups devrait frapper : {env.Caractere(0):F2}");
    }

    [Fact]
    public void Une_source_qui_tient_a_un_caractere_haut()
    {
        var env = new SourceEnvelope(1, Image);
        for (var i = 0; i < 2 * VingtSecondes; i++)
            env.Feed(0, 0.7f + 0.05f * MathF.Sin(i / 30f));
        Assert.True(env.Caractere(0) > 0.8f, $"une nappe devrait tenir : {env.Caractere(0):F2}");
    }

    [Fact]
    public void Le_caractere_ne_change_pas_sur_une_image()
    {
        var env = new SourceEnvelope(1, Image);
        for (var i = 0; i < 2 * VingtSecondes; i++) env.Feed(0, 0.7f);
        var avant = env.Caractere(0);
        env.Feed(0, 0.02f);
        env.Feed(0, 1f);
        Assert.True(MathF.Abs(env.Caractere(0) - avant) < 0.05f,
            $"une seule montee ne fait pas d'une nappe une frappe : {avant:F2} -> {env.Caractere(0):F2}");
    }
}
