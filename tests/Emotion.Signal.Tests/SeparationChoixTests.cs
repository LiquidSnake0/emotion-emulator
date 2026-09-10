using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// Le nombre de sources est DECOUVERT, pas impose. C'est la demande du DJ :
///
/// > « Comme ca on aura toutes les sources d'un morceau, qui n'est jamais plafonne a 6 :
/// >   des fois on en a 2, des fois 8, c'est justement ce que le programme est cense me
/// >   dire. »
///
/// On fabrique donc un signal dont on CONNAIT le nombre de sources, et l'on regarde si la
/// separation le retrouve. C'est le meme principe que le metronome fabrique pour le tempo :
/// juger sur de la matiere ambigue a coute des semaines a ce projet, et une journee entiere
/// tout recemment.
/// </summary>
public class SeparationChoixTests
{
    private const int Bins = 128;
    private const int Rate = 16_000;

    /// <summary>Un profil : une bosse large, centree sur un registre.</summary>
    private static float[] Profil(float centre, float largeur)
    {
        var p = new float[Bins];
        for (var b = 0; b < Bins; b++)
        {
            var d = (b - centre) / largeur;
            p[b] = 0.02f + MathF.Exp(-0.5f * d * d);
        }
        return p;
    }

    /// <summary>
    /// Une image de spectre : chaque source joue a un niveau qui lui est propre a cet
    /// instant. Les niveaux changent d'une image a l'autre — c'est ce qui rend les sources
    /// separables : deux profils qui montent et descendent ENSEMBLE ne seraient qu'un.
    /// </summary>
    private static float[] Image(IReadOnlyList<float[]> profils, Random alea)
    {
        var v = new float[Bins];
        foreach (var p in profils)
        {
            var g = (float)alea.NextDouble();
            for (var b = 0; b < Bins; b++) v[b] += g * p[b];
        }
        return v;
    }

    /// <summary>
    /// Nourrit la separation jusqu'a ce que le choix ait eu lieu, apprentissage en ligne
    /// pour que tout se passe dans le fil du test.
    /// </summary>
    private static SourceSeparator Apprendre(IReadOnlyList<float[]> profils, int memoire = 256)
    {
        var sep = new SourceSeparator(Bins, Rate, memoire) { ApprentissageEnLigne = true };
        var alea = new Random(4);
        // Le provisoire part a memoire/2 images, le choix quand la memoire est pleine puis
        // encore memoire/2 plus tard, et l'adoption a l'image suivante. Deux memoires
        // suffisent largement.
        for (var t = 0; t < 2 * memoire + 8; t++)
            sep.Feed(Image(profils, alea));
        return sep;
    }

    [Fact]
    public void Trois_sources_franches_donnent_trois()
    {
        var profils = new[] { Profil(12f, 5f), Profil(50f, 7f), Profil(100f, 6f) };
        var sep = Apprendre(profils);

        Assert.True(sep.Pret, "la separation n'a rien appris");
        Assert.True(sep.ChoixFait, "le balayage n'a pas eu lieu");
        Assert.Equal(3, sep.Actives);
    }

    [Fact]
    public void Deux_sources_donnent_deux_et_non_six()
    {
        // C'EST LE CAS QUI COMPTE. Avant, six cases s'allumaient quoi qu'il arrive, et
        // quatre d'entre elles montraient du bruit avec la meme conviction que les deux
        // vraies.
        var profils = new[] { Profil(20f, 6f), Profil(90f, 8f) };
        var sep = Apprendre(profils);

        Assert.True(sep.Pret);
        Assert.Equal(2, sep.Actives);
        Assert.True(sep.Bilans.Count >= 2, "le balayage doit avoir essaye au moins deux nombres");
    }

    [Fact]
    public void Les_rangs_au_dela_des_actives_rendent_zero()
    {
        var sep = Apprendre(new[] { Profil(20f, 6f), Profil(90f, 8f) });
        Assert.Equal(2, sep.Actives);

        // Une image de plus, pour que les activations soient celles du regime choisi.
        sep.Feed(Image(new[] { Profil(20f, 6f), Profil(90f, 8f) }, new Random(9)));

        for (var r = sep.Actives; r < SourceSeparator.Sources; r++)
        {
            Assert.Equal(0f, sep.ActivationOrdonnee(r));
            Assert.Equal(0f, sep.EcouteOrdonnee(r));
            Assert.Equal(0f, sep.StabiliteOrdonnee(r));
        }
        // Et les actives, elles, portent quelque chose.
        Assert.True(sep.ActivationOrdonnee(0) > 0f || sep.ActivationOrdonnee(1) > 0f);
    }

    [Fact]
    public void Un_nouveau_disque_efface_tout()
    {
        var sep = Apprendre(new[] { Profil(12f, 5f), Profil(50f, 7f), Profil(100f, 6f) });
        Assert.Equal(3, sep.Actives);

        // Reset n'etait appele nulle part : les profils du disque precedent servaient de
        // point de depart au suivant. Ici on verifie qu'il remet bien tout a zero.
        sep.Reset();
        Assert.False(sep.Pret);
        Assert.Equal(0, sep.Actives);
        Assert.False(sep.ChoixFait);
        for (var r = 0; r < SourceSeparator.Sources; r++)
            Assert.Equal(0f, sep.ActivationOrdonnee(r));
    }

    [Fact]
    public void Le_bilan_montre_un_coude_et_pas_un_caprice()
    {
        var sep = Apprendre(new[] { Profil(12f, 5f), Profil(50f, 7f), Profil(100f, 6f) });

        // Le reste doit baisser jusqu'a trois, puis ne presque plus bouger : c'est CELA
        // qu'on appelle un coude, et c'est ce que l'ecran doit pouvoir expliquer.
        var bilans = sep.Bilans;
        Assert.True(bilans.Count >= 3);
        var deux = bilans.First(b => b.K == 2).Reste;
        var trois = bilans.First(b => b.K == 3).Reste;
        Assert.True(trois < deux - 0.015f, $"passer de 2 a 3 devrait expliquer davantage : {deux:F3} -> {trois:F3}");
        if (bilans.Any(b => b.K == 4))
        {
            var quatre = bilans.First(b => b.K == 4).Reste;
            Assert.True(trois - quatre < 0.015f || bilans.First(b => b.K == 4).Doublon >= 0.90f,
                $"une quatrieme source ne devrait plus rien expliquer de neuf : {trois:F3} -> {quatre:F3}");
        }
    }
}
