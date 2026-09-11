using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>La gamme de la fiche Camelot : un prior doux, et des degres pour le rendu.</summary>
public class GammeTests
{
    [Theory]
    [InlineData("8A", 9, true)]     // la mineur
    [InlineData("8B", 0, false)]    // do majeur
    [InlineData("9A", 4, true)]     // mi mineur
    [InlineData("1B", 11, false)]   // si majeur
    [InlineData("12A", 1, true)]    // do# mineur
    public void Le_tag_camelot_donne_la_tonique_et_le_mode(string camelot, int tonique, bool mineur)
    {
        var g = Gamme.Lire(camelot);
        Assert.NotNull(g);
        Assert.Equal(tonique, g.Value.Tonique);
        Assert.Equal(mineur, g.Value.Mineur);
    }

    [Theory]
    [InlineData("")]
    [InlineData("13A")]
    [InlineData("8C")]
    [InlineData(null)]
    public void Un_tag_qui_n_en_est_pas_un_ne_donne_rien(string? camelot)
    {
        Assert.Null(Gamme.Lire(camelot));
        Assert.Null(Gamme.Classes(camelot));
        Assert.Equal(Gamme.Inconnu, Gamme.Degre(camelot, 0));
    }

    [Fact]
    public void Les_degres_de_la_mineur()
    {
        // la si do re mi fa sol : I II III IV V VI VII ; do# n'y est pas.
        Assert.Equal(0, Gamme.Degre("8A", 9));
        Assert.Equal(2, Gamme.Degre("8A", 0));
        Assert.Equal(4, Gamme.Degre("8A", 4));
        Assert.Equal(6, Gamme.Degre("8A", 7));
        Assert.Equal(Gamme.HorsGamme, Gamme.Degre("8A", 1));
        Assert.Equal("V", Gamme.Nom(4));
    }

    [Fact]
    public void Le_relatif_majeur_partage_les_memes_notes()
    {
        var mineur = Gamme.Classes("8A")!;
        var majeur = Gamme.Classes("8B")!;
        Assert.Equal(mineur, majeur);
    }
}

/// <summary>
/// Le degre publie par source vient des gabarits : un instrument fabrique qui joue une note
/// connue, dans une gamme connue, doit rendre le bon degre.
/// </summary>
public class DegreParSourceTests
{
    private const int Rate = 16_000;
    private const int Hop = 512;
    private static readonly int[] DemiTonsLaMineur = [0, 2, 3, 5, 7, 8, 10];   // I II III IV V VI VII

    [Fact]
    public void Les_degres_publies_sont_ceux_des_notes_jouees()
    {
        // Trois instruments qui parcourent la gamme de la mineur chacun dans son octave (la1,
        // la3, la5), avec leurs harmoniques propres ; puis chacun se pose sur une note connue :
        // la = I, do = III, mi = V. Les degres publies, du grave a l'aigu, doivent le dire.
        var sep = new SourceSeparator(Rate, Hop, 300) { ApprentissageEnLigne = true };
        sep.Gamme("8A");
        float[] grave = [55f, 220f, 880f];
        float[][] harm = [[1f, 0.5f, 0.2f, 0.08f], [1f, 0.3f, 0.6f, 0.15f, 0.25f, 0.05f, 0.1f], [0.6f, 0.9f, 1f, 0.9f, 0.7f]];
        var phases = new double[3][]; for (var i = 0; i < 3; i++) phases[i] = new double[harm[i].Length];
        var alea = new Random(4);
        var hz = new float[3]; var gain = new float[3]; var reste = new int[3];
        var bloc = new float[Hop];
        void Image()
        {
            Array.Clear(bloc);
            for (var i = 0; i < 3; i++)
                for (var k = 0; k < harm[i].Length; k++)
                {
                    var pas = 2 * Math.PI * hz[i] * (k + 1) / Rate; var ph = phases[i][k]; var a = gain[i] * harm[i][k] * 0.1f;
                    for (var j = 0; j < Hop; j++) { bloc[j] += a * (float)Math.Sin(ph); ph += pas; }
                    phases[i][k] = ph % (2 * Math.PI);
                }
            sep.Feed(bloc);
        }
        for (var t = 0; t < 2 * 300 + 16; t++)
        {
            for (var i = 0; i < 3; i++)
                if (reste[i]-- <= 0)
                {
                    hz[i] = grave[i] * MathF.Pow(2f, DemiTonsLaMineur[alea.Next(7)] / 12f);
                    gain[i] = 0.3f + 0.7f * (float)alea.NextDouble(); reste[i] = alea.Next(6, 20);
                }
            Image();
        }
        Assert.Equal(3, sep.Actives);

        // Chacun se pose : la (I), do (III), mi (V).
        int[] attendus = [0, 2, 4]; int[] demiTons = [0, 3, 7];
        for (var i = 0; i < 3; i++) { hz[i] = grave[i] * MathF.Pow(2f, demiTons[i] / 12f); gain[i] = 0.8f; }
        for (var t = 0; t < 40; t++) Image();
        var degres = Enumerable.Range(0, 3).Select(sep.DegreOrdonne).ToArray();
        var diag = string.Join(" | ", Enumerable.Range(0, 3).Select(r =>
            $"rang {r}: classe {sep.ClasseOrdonnee(r)}, position {sep.PositionOrdonnee(r).Position:F1}, centre {sep.PositionOrdonnee(r).Centre:F1}, {SourceSeparator.CaseEnHz(sep.PositionOrdonnee(r).Position + sep.PositionOrdonnee(r).Centre):F0} Hz"));
        // Le piano et le clair sont resolus : leur degre est du. La basse joue sous la
        // resolution de l'axe ; on accepte qu'elle s'abstienne (inconnu), jamais qu'elle
        // se trompe.
        Assert.True(degres[1] == 2 && degres[2] == 4 && (degres[0] == 0 || degres[0] == Gamme.Inconnu),
            $"degres {string.Join(",", degres)} ; accordage {sep.AccordageCents} cents ; {diag}");
    }
}
