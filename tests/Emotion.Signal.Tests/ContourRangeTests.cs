using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class ContourRangeTests
{
    private static float Rouler(ContourRange e, int rang, IEnumerable<float> suite)
    {
        var dernier = 0f;
        foreach (var v in suite) dernier = e.Situer(rang, v, actif: true);
        return dernier;
    }

    /// <summary>
    /// Le cas qui a motive la classe : la troisieme source de Macroblank parcourt 0,473 a
    /// 0,692, soit un cinquieme de sa case. Rapportee a son etendue, elle doit en occuper
    /// la plus grande part — sans qu'on ait rien invente, puisque les extremes restent les
    /// siens.
    ///
    /// Elle n'occupe pas la case <b>entiere</b>, et c'est voulu : 1,75 octave se situe
    /// entre le seuil d'une octave et les deux octaves de l'etirement plein, donc le rendu
    /// est aux trois quarts du chemin. Une source qui s'ouvre au fil du morceau glisse ainsi
    /// vers son etendue au lieu d'y sauter.
    /// </summary>
    [Fact]
    public void Une_source_qui_parcourt_un_cinquieme_occupe_la_plus_grande_part_de_sa_case()
    {
        var e = new ContourRange(6);
        var aller = Enumerable.Range(0, 200).Select(i => 0.473f + 0.219f * (i % 100) / 99f);
        Rouler(e, 2, aller);

        var bas = e.Situer(2, 0.473f, true);
        var haut = e.Situer(2, 0.692f, true);
        var course = haut - bas;

        Assert.True(course > 0.65f, $"course de {course:F2}, il en fallait bien plus que les 0,22 d'origine");
        Assert.InRange(e.Situer(2, 0.582f, true), 0.42f, 0.58f);
    }

    /// <summary>
    /// ET LE GARDE-FOU. Une nappe tenue ne produit qu'un tremblement de mesure. L'etirer
    /// en ferait un mouvement plein cadre : du bruit affiche comme du sens. En dessous
    /// d'une octave d'etendue, on rend la hauteur brute.
    /// </summary>
    [Fact]
    public void Une_source_qui_ne_bouge_pas_n_est_pas_etiree()
    {
        var e = new ContourRange(6);
        var alea = new Random(7);
        var tremble = Enumerable.Range(0, 300).Select(_ => 0.60f + (float)(alea.NextDouble() - 0.5) * 0.01f);
        Rouler(e, 0, tremble);

        var rendu = e.Situer(0, 0.603f, true);
        Assert.InRange(rendu, 0.58f, 0.62f);        // reste la hauteur brute
        Assert.True(e.Etendue(0) < ContourRange.EtendueMin);
    }

    /// <summary>
    /// Avant d'avoir assez ecoute, on rend le brut. Deux observations suffiraient sinon a
    /// definir une etendue, et la premiere note du morceau deciderait de tout l'affichage.
    /// </summary>
    [Fact]
    public void Avant_d_avoir_assez_ecoute_on_rend_le_brut()
    {
        var e = new ContourRange(6);
        Assert.Equal(0.30f, e.Situer(1, 0.30f, true), 3);
        Assert.Equal(0.80f, e.Situer(1, 0.80f, true), 3);
    }

    /// <summary>
    /// Une source muette ne dit rien de son etendue. L'ecouter quand meme ferait descendre
    /// toutes les bornes vers la valeur au repos.
    /// </summary>
    [Fact]
    public void Une_source_muette_n_apprend_rien()
    {
        var e = new ContourRange(6);
        for (var i = 0; i < 200; i++) e.Situer(3, 0.2f + 0.5f * (i % 2), actif: false);
        Assert.Equal(0f, e.Etendue(3));
    }

    /// <summary>
    /// Le vinyle est range : ce qu'il parcourait ne sert plus a rien. C'est la regle du
    /// projet, et elle vaut ici comme ailleurs.
    /// </summary>
    [Fact]
    public void Oublier_efface_les_etendues()
    {
        var e = new ContourRange(6);
        Rouler(e, 4, Enumerable.Range(0, 200).Select(i => 0.2f + 0.6f * (i % 50) / 49f));
        Assert.True(e.Etendue(4) > 0.5f);

        e.Oublier();
        Assert.Equal(0f, e.Etendue(4));
        Assert.Equal(0.42f, e.Situer(4, 0.42f, true), 3);
    }
}
