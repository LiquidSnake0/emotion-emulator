using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class EventFamilyTests
{
    /// <summary>
    /// Deux frappes du meme instrument tombent dans la meme famille, meme frappees
    /// inegalement. C'est toute la raison d'etre du mecanisme : le rendu doit pouvoir
    /// reconnaitre un instrument sans qu'on le nomme.
    /// </summary>
    [Fact]
    public void Deux_frappes_du_meme_instrument_se_rejoignent()
    {
        var f = new EventFamilies();

        var a = f.Ranger(new EventSignature(0.30f, 0.40f, 0.20f));
        var b = f.Ranger(new EventSignature(0.34f, 0.43f, 0.17f));   // meme, un peu plus fort

        Assert.Equal(a, b);
        Assert.Equal(1, f.Connues);
        Assert.Equal(2, f.VuesDe(a));
    }

    /// <summary>
    /// Et deux instruments differents se separent — y compris quand ils ont la meme
    /// couleur et ne different que par leur duree, ce que les bandes de frequence ne
    /// distinguaient pas. C'est le cas releve sur un morceau du crate : deux familles de
    /// brillance 0,46 et 0,51 mais de piquant 0,04 et 0,23.
    /// </summary>
    [Fact]
    public void Deux_instruments_de_meme_couleur_mais_de_duree_differente_se_separent()
    {
        var f = new EventFamilies();

        var court = f.Ranger(new EventSignature(0.48f, 0.41f, 0.05f));
        var long_ = f.Ranger(new EventSignature(0.50f, 0.42f, 0.60f));

        Assert.NotEqual(court, long_);
        Assert.Equal(2, f.Connues);
    }

    /// <summary>
    /// Le centre d'une famille se pose au lieu de flotter : la millieme frappe corrige
    /// moins que la dixieme. Sans cela, une frappe atypique deplacerait durablement la
    /// famille entiere.
    /// </summary>
    [Fact]
    public void Le_centre_se_pose_au_lieu_de_flotter()
    {
        var f = new EventFamilies();
        for (var i = 0; i < 200; i++) f.Ranger(new EventSignature(0.40f, 0.40f, 0.40f));

        var avant = f.CentreDe(0);
        f.Ranger(new EventSignature(0.55f, 0.40f, 0.40f));       // une frappe atypique
        var apres = f.CentreDe(0);

        Assert.True(MathF.Abs(apres.Brillance - avant.Brillance) < 0.01f,
                    $"le centre a bouge de {apres.Brillance - avant.Brillance:F4}");
    }

    /// <summary>
    /// Au-dela du maximum, c'est la famille la moins vue qui cede : elle a le plus de
    /// chances d'etre un accident — un craquement de vinyle, une frappe isolee.
    /// </summary>
    [Fact]
    public void La_famille_la_moins_vue_cede_sa_place()
    {
        var f = new EventFamilies();

        // Huit familles bien separees : les huit coins du cube, distants de 0,9 sur au
        // moins un axe. Les espacer de 0,1 sur un seul axe, comme le faisait une premiere
        // version de ce test, les faisait fusionner — a juste titre, puisque le seuil de
        // regroupement vaut 0,25.
        for (var i = 0; i < EventFamilies.Max; i++)
        {
            var s = new EventSignature(
                (i & 1) * 0.9f, ((i >> 1) & 1) * 0.9f, ((i >> 2) & 1) * 0.9f);
            f.Ranger(s);
            if (i == 0) for (var n = 0; n < 50; n++) f.Ranger(s);
        }

        Assert.Equal(EventFamilies.Max, f.Connues);
        var vuesAvant = f.VuesDe(0);

        // Une neuvieme, au centre du cube : loin de tous les coins.
        f.Ranger(new EventSignature(0.45f, 0.45f, 0.45f));

        Assert.Equal(EventFamilies.Max, f.Connues);
        Assert.Equal(vuesAvant, f.VuesDe(0));                      // la plus vue est intacte
    }
}
