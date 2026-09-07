using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class DeckTests
{
    private static TrackContext Track(string family, string title = "x") =>
        new(title, "disc", "A", "8A", family, "#000000", null);

    [Fact]
    public void Caler_au_casque_ne_change_pas_ce_qui_est_projete()
    {
        // La regle qui protege le set : si la projection suivait la selection, le
        // public verrait le beatmatch commencer, c'est-a-dire la coulisse.
        var deck = new Deck(Track("M-", "en cours"), null);

        var after = deck.Cue(Track("M+", "prepare"));

        Assert.Equal("en cours", after.Playing.Title);
        Assert.Equal(Kind.Waves, after.Playing.Scene.Kind);
        Assert.Equal("prepare", after.Cued!.Title);
    }

    [Fact]
    public void Basculer_promeut_le_prepare_et_libere_la_place()
    {
        var deck = new Deck(Track("M-"), null).Cue(Track("M+", "suivant"));

        var after = deck.Take();

        Assert.Equal("suivant", after.Playing.Title);
        Assert.Equal(Kind.Thunder, after.Playing.Scene.Kind);
        Assert.Null(after.Cued);
    }

    [Fact]
    public void Basculer_sans_rien_de_prepare_ne_coupe_pas_la_projection()
    {
        // Un take de trop en plein set ne doit pas eteindre le mur.
        var deck = new Deck(Track("M-", "en cours"), null);

        var after = deck.Take();

        Assert.Equal("en cours", after.Playing.Title);
        Assert.Null(after.Cued);
    }

    [Fact]
    public void Renoncer_laisse_le_morceau_en_cours_intact()
    {
        var deck = new Deck(Track("M-", "en cours"), null).Cue(Track("M+"));

        var after = deck.Drop();

        Assert.Equal("en cours", after.Playing.Title);
        Assert.Null(after.Cued);
    }

    [Fact]
    public void Le_deck_vide_projette_un_repos_pas_une_erreur()
    {
        // Au lancement, avant le premier disque, le mur doit montrer quelque chose.
        Assert.Equal(Kind.Rest, Deck.Empty.Playing.Scene.Kind);
        Assert.Null(Deck.Empty.Cued);
    }

    [Theory]
    [InlineData("M-", Kind.Waves)]
    [InlineData("M+", Kind.Thunder)]
    [InlineData("B", Kind.Grove)]
    [InlineData("V", Kind.Nebula)]
    [InlineData("inconnue", Kind.Rest)]
    [InlineData(null, Kind.Rest)]
    public void Une_famille_inconnue_retombe_sur_le_repos(string? family, Kind expected)
    {
        // Un crate en cours de correction contient des familles vides : elles ne
        // doivent pas eteindre l'ecran au milieu d'un morceau.
        Assert.Equal(expected, Scene.ForFamily(family).Kind);
    }
}
