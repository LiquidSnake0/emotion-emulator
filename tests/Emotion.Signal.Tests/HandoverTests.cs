using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class HandoverTests
{
    [Fact]
    public void Le_relais_donne_un_tempo_tout_de_suite()
    {
        // Le but du passage de relais : ne pas laisser le master chercher pendant le
        // passage le plus visible du set. Un estimateur parti de rien met une a deux
        // secondes a accrocher ; celui-ci repond des la premiere image.
        var t = new TempoEstimator();
        Assert.Null(t.Bpm);

        t.Adopt(87f, 10_000);

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 85f, 89f);
    }

    [Fact]
    public void Le_master_reprend_la_main_sur_le_tempo_repris()
    {
        // La nuance qui compte : le relais amorce, il ne verrouille pas. Pendant le
        // beatmatch le pitch a bouge — c'est le but du geste — donc ce que le cue a
        // entendu n'est deja plus ce qui sort en salle. Les attaques reelles doivent
        // reprendre la main en quelques mesures.
        var t = new TempoEstimator();
        t.Adopt(87f, 0);

        // Le disque sort en fait a 94 BPM, pitche pendant le calage.
        const int gap = 638;
        var at = 0L;
        for (var i = 0; i < 20; i++) { at += gap; t.Mark(at); }

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 92f, 96f);
    }

    [Fact]
    public void Un_tempo_aberrant_n_est_pas_repris()
    {
        // Un cue mal accroche ne doit pas empoisonner le master.
        var t = new TempoEstimator();
        t.Adopt(15f, 0);
        Assert.Null(t.Bpm);
    }

    [Fact]
    public void Le_relais_recale_aussi_la_phase()
    {
        // Sans ancre, la phase resterait celle du morceau precedent et les figures a
        // l'echelle de la mesure tomberaient a cote pendant tout le debut du disque.
        var t = new TempoEstimator();
        t.Adopt(87f, 5_000);

        var phase = t.Phase(5_000);
        Assert.NotNull(phase);
        Assert.InRange(phase!.Value, 0f, 0.02f);
    }
}
