using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// La frappe d'une source vient de son propre niveau : une montee franche, pas la bande de
/// frequence du meme rang. C'est ce que le GPU recevra comme morse.
/// </summary>
public class SourceEnvelopeFrappeTests
{
    private const float Image = 1024f / 48_000f;

    [Fact]
    public void Une_montee_franche_est_une_frappe_et_une_seule()
    {
        var env = new SourceEnvelope(1, Image);
        for (var i = 0; i < 20; i++) env.Feed(0, 0.10f);
        Assert.False(env.Frappe(0));

        env.Feed(0, 0.80f);
        Assert.True(env.Frappe(0), "la montee de 0,1 a 0,8 en une image est une frappe");

        // Elle tient : ce n'est plus une frappe, c'est la meme note.
        env.Feed(0, 0.80f);
        Assert.False(env.Frappe(0));
    }

    [Fact]
    public void Une_montee_lente_n_est_pas_une_frappe()
    {
        var env = new SourceEnvelope(1, Image);
        // Un souffle : de 0 a 1 en cinquante images. Aucune image ne monte assez pour
        // compter comme une attaque.
        for (var i = 0; i <= 50; i++)
        {
            env.Feed(0, i / 50f);
            Assert.False(env.Frappe(0), $"image {i} : une montee de 2 % par image n'est pas une frappe");
        }
    }

    [Fact]
    public void Deux_frappes_trop_proches_n_en_font_qu_une()
    {
        var env = new SourceEnvelope(1, Image);
        for (var i = 0; i < 10; i++) env.Feed(0, 0.05f);
        env.Feed(0, 0.70f);
        Assert.True(env.Frappe(0));
        env.Feed(0, 0.05f);
        env.Feed(0, 0.70f);            // deux images plus tard : le repos n'est pas ecoule
        Assert.False(env.Frappe(0));
        for (var i = 0; i < SourceEnvelope.ReposFrappe + 1; i++) env.Feed(0, 0.05f);
        env.Feed(0, 0.70f);
        Assert.True(env.Frappe(0), "apres le repos, une nouvelle montee compte");
    }

    [Fact]
    public void Chaque_source_a_sa_propre_frappe()
    {
        var env = new SourceEnvelope(2, Image);
        for (var i = 0; i < 10; i++) { env.Feed(0, 0.05f); env.Feed(1, 0.60f); }
        env.Feed(0, 0.70f);
        env.Feed(1, 0.60f);
        Assert.True(env.Frappe(0));
        Assert.False(env.Frappe(1), "la source 2 tient, elle ne frappe pas parce que la 1 frappe");
    }
}
