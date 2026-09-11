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
/// juger sur de la matiere ambigue a coute des semaines a ce projet.
///
/// LES SOURCES FABRIQUEES SONT DES INSTRUMENTS, PAS DES BANDES. Chacune joue des NOTES —
/// une hauteur qui change toutes les quelques dizaines d'images dans son octave — avec une
/// forme d'harmoniques qui lui est propre. C'est exactement ce qui faisait echouer
/// l'ancien profil fige : une basse qui change de note etait deux profils. Un gabarit qui
/// glisse doit n'en faire qu'un.
/// </summary>
public class SeparationChoixTests
{
    private const int Rate = 16_000;
    private const int Hop = 512;

    /// <summary>
    /// Un instrument : une octave ou il joue, et le poids de ses harmoniques.
    /// </summary>
    private sealed record Instrument(float GraveHz, float[] Harmoniques);

    private static readonly Instrument Basse = new(55f, [1f, 0.5f, 0.2f, 0.08f]);
    private static readonly Instrument Piano = new(220f, [1f, 0.3f, 0.6f, 0.15f, 0.25f, 0.05f, 0.1f]);
    private static readonly Instrument Clair = new(880f, [0.6f, 0.9f, 1f, 0.9f, 0.7f]);

    /// <summary>
    /// Nourrit la separation avec les instruments donnes, jusqu'a ce que le choix ait eu
    /// lieu, apprentissage en ligne pour que tout se passe dans le fil du test.
    /// </summary>
    private static SourceSeparator Apprendre(IReadOnlyList<Instrument> instruments, int memoire = 300)
    {
        var sep = new SourceSeparator(Rate, Hop, memoire) { ApprentissageEnLigne = true };
        var alea = new Random(4);
        var n = instruments.Count;
        var phases = new double[n][];
        var hz = new float[n];
        var gain = new float[n];
        var reste = new int[n];
        for (var i = 0; i < n; i++) phases[i] = new double[instruments[i].Harmoniques.Length];

        // Le provisoire part a memoire/2 images, le choix quand la memoire est pleine puis
        // encore memoire/2 plus tard, et l'adoption a l'image suivante. Deux memoires et
        // quelques images suffisent largement.
        var bloc = new float[Hop];
        for (var t = 0; t < 2 * memoire + 16; t++)
        {
            for (var i = 0; i < n; i++)
            {
                if (reste[i]-- > 0) continue;
                // Une nouvelle note : un demi-ton au hasard dans l'octave, un niveau au
                // hasard, tenue entre six et vingt images. Les instruments changent de note a
                // des moments differents — c'est ce qui les rend separables.
                var demiTon = alea.Next(0, 12);
                hz[i] = instruments[i].GraveHz * MathF.Pow(2f, demiTon / 12f);
                gain[i] = 0.3f + 0.7f * (float)alea.NextDouble();
                reste[i] = alea.Next(6, 20);
            }
            Array.Clear(bloc);
            for (var i = 0; i < n; i++)
            {
                var harm = instruments[i].Harmoniques;
                for (var k = 0; k < harm.Length; k++)
                {
                    var pas = 2 * Math.PI * hz[i] * (k + 1) / Rate;
                    var ph = phases[i][k];
                    var a = gain[i] * harm[k] * 0.1f;
                    for (var j = 0; j < Hop; j++)
                    {
                        bloc[j] += a * (float)Math.Sin(ph);
                        ph += pas;
                    }
                    phases[i][k] = ph % (2 * Math.PI);
                }
            }
            sep.Feed(bloc);
        }
        return sep;
    }

    private static string Bilans(SourceSeparator sep) =>
        string.Join(" | ", sep.Historique.Select(h => string.Join("  ",
            h.Select(b => $"{b.K}:{100 * b.Reste:F2}%/{b.Doublon:F2}/lien {b.Lien:F2}"))));

    [Fact]
    public void Trois_instruments_donnent_trois()
    {
        var sep = Apprendre([Basse, Piano, Clair]);

        Assert.True(sep.Pret, "la separation n'a rien appris");
        Assert.True(sep.ChoixFait, "le balayage n'a pas eu lieu");
        Assert.True(sep.Actives == 3, $"trois instruments, {sep.Actives} sources : {Bilans(sep)}");
    }

    [Fact]
    public void Deux_instruments_donnent_deux_et_non_six()
    {
        // C'EST LE CAS QUI COMPTE. Avant, six cases s'allumaient quoi qu'il arrive, et
        // quatre d'entre elles montraient du bruit avec la meme conviction que les deux
        // vraies.
        var sep = Apprendre([Basse, Piano]);

        Assert.True(sep.Pret);
        Assert.True(sep.Actives == 2, $"deux instruments, {sep.Actives} sources : {Bilans(sep)}");
        Assert.True(sep.Bilans.Count >= 2, "le balayage doit avoir essaye au moins deux nombres");
    }

    [Fact]
    public void Un_instrument_qui_change_de_note_reste_une_source()
    {
        // LA RAISON D'ETRE DU GABARIT. Une basse seule qui parcourt son octave etait, pour
        // le profil fige, autant de sources que de notes. Ici elle doit n'en faire qu'une —
        // ou deux au pire, pas quatre.
        var sep = Apprendre([Basse]);

        Assert.True(sep.Pret);
        Assert.True(sep.Actives <= 2, $"une basse seule, {sep.Actives} sources : {Bilans(sep)}");
    }

    [Fact]
    public void Les_rangs_au_dela_des_actives_rendent_zero()
    {
        var sep = Apprendre([Basse, Piano]);
        Assert.Equal(2, sep.Actives);

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
    public void Les_sources_sont_rangees_du_grave_a_l_aigu()
    {
        var sep = Apprendre([Basse, Piano, Clair]);
        Assert.Equal(3, sep.Actives);

        // La hauteur publiee est une vraie hauteur : la basse sous le piano, le piano sous
        // le clair. Avec des octaves entieres entre eux, l'ordre ne peut pas etre un hasard.
        Assert.True(sep.HauteurOrdonnee(0) < sep.HauteurOrdonnee(1),
            $"grave {sep.HauteurOrdonnee(0):F2} devrait etre sous {sep.HauteurOrdonnee(1):F2}");
        Assert.True(sep.HauteurOrdonnee(1) < sep.HauteurOrdonnee(2),
            $"medium {sep.HauteurOrdonnee(1):F2} devrait etre sous {sep.HauteurOrdonnee(2):F2}");
    }

    [Fact]
    public void Un_nouveau_disque_efface_tout()
    {
        var sep = Apprendre([Basse, Piano, Clair]);
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
        var sep = Apprendre([Basse, Piano, Clair]);

        // Le reste doit baisser jusqu'a trois, puis ne presque plus bouger : c'est CELA
        // qu'on appelle un coude, et c'est ce que l'ecran doit pouvoir expliquer. On lit le
        // PREMIER bilan, celui du balayage : les suivants sont ceux de la croissance, qui
        // n'essaie que K et K+1.
        var bilans = sep.Historique[0];
        Assert.True(bilans.Count >= 3);
        var deux = bilans.First(b => b.K == 2).Reste;
        var trois = bilans.First(b => b.K == 3).Reste;
        Assert.True(trois < deux, $"passer de 2 a 3 devrait expliquer davantage : {deux:F4} -> {trois:F4}");
        if (bilans.Any(b => b.K == 4))
        {
            var quatre = bilans.First(b => b.K == 4);
            Assert.True(trois - quatre.Reste < deux - trois,
                $"une quatrieme source ne devrait plus expliquer autant de neuf : {deux:F4} -> {trois:F4} -> {quatre.Reste:F4}");
        }
    }
}
