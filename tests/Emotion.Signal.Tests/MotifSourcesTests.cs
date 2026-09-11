using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// Le motif d'une source : ou, dans la mesure, elle monte — et le verrou qui en decoule.
/// « Reperer les patterns de repetition. »
/// </summary>
public class MotifSourcesTests
{
    private const int ImagesParMesure = 64;

    /// <summary>Joue <paramref name="mesures"/> mesures ; la source 0 monte aux cases donnees.</summary>
    private static MotifSources Jouer(int mesures, int[] casesSource0, int[]? casesSource1 = null, Random? bruit = null)
    {
        var m = new MotifSources(4);
        for (var b = 0; b < mesures; b++)
            for (var i = 0; i < ImagesParMesure; i++)
            {
                var phase = i / (float)ImagesParMesure;
                var c = (int)(phase * MotifSources.Cases);
                var debutDeCase = i % (ImagesParMesure / MotifSources.Cases) == 0;
                // Une montee = un niveau qui saute puis retombe. Ailleurs, un fond plat (ou du bruit).
                var n0 = casesSource0.Contains(c) && debutDeCase ? 1f : 0.1f;
                var n1 = casesSource1 is not null && casesSource1.Contains(c) && debutDeCase ? 1f : 0.1f;
                if (bruit is not null) { n0 = (float)bruit.NextDouble(); n1 = (float)bruit.NextDouble(); }
                m.Feed(0, n0, phase);
                m.Feed(1, n1, phase);
            }
        return m;
    }

    [Fact]
    public void Un_motif_qui_se_repete_est_retrouve_case_par_case()
    {
        var m = Jouer(MotifSources.Mesures + 2, [0, 8]);
        Assert.Equal(0b0000_0001_0000_0001, m.Masque(0));
        Assert.True(m.Stabilite(0) > 0.95f, $"stabilite {m.Stabilite(0):F2}");
    }

    [Fact]
    public void Du_hasard_ne_fait_pas_un_motif()
    {
        var m = Jouer(MotifSources.Mesures + 2, [], bruit: new Random(3));
        Assert.True(m.Stabilite(0) < 0.6f, $"le hasard ne devrait pas tenir : {m.Stabilite(0):F2}");
        Assert.False(m.Verrouille(2));
    }

    [Fact]
    public void Le_verrou_tombe_quand_deux_sources_tiennent_sur_huit_mesures()
    {
        var court = Jouer(MotifSources.Mesures - 3, [0, 8], [4, 12]);
        Assert.False(court.Verrouille(2), "trop peu de mesures ne suffisent pas");
        var long_ = Jouer(MotifSources.Mesures + 2, [0, 8], [4, 12]);
        Assert.True(long_.Verrouille(2), $"deux motifs tenus : {long_.Stabilite(0):F2} et {long_.Stabilite(1):F2}");
        Assert.False(long_.Verrouille(1), "une seule source publiee ne verrouille pas");
    }

    [Fact]
    public void Sans_grille_rien_ne_s_accumule()
    {
        var m = new MotifSources(2);
        for (var i = 0; i < 500; i++) m.Feed(0, i % 7 == 0 ? 1f : 0.1f, null);
        Assert.Equal(0, m.Masque(0));
        Assert.Equal(0, m.MesuresVues(0));
    }

    [Fact]
    public void Un_nouveau_disque_efface_le_motif()
    {
        var m = Jouer(MotifSources.Mesures + 2, [0, 8]);
        m.Reset();
        Assert.Equal(0, m.Masque(0));
        Assert.Equal(0f, m.Stabilite(0));
    }
}
