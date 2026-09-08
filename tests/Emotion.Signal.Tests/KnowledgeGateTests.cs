using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class KnowledgeGateTests
{
    private const long Step = 21;

    /// <summary>Joue des grandeurs stables pendant une duree donnee.</summary>
    private static (KnowledgeGate Gate, Readiness Last) PlayStable(float seconds)
    {
        var gate = new KnowledgeGate();
        var last = Readiness.None;

        for (long t = 0; t < seconds * 1000; t += Step)
            last = gate.Feed(t, 96f, 0.5f, 0.4f, 0.35f);

        return (gate, last);
    }

    [Fact]
    public void Un_disque_stable_donne_le_feu_vert_par_convergence()
    {
        // Quatre grandeurs qui ne bougent plus pendant une seconde chacune : le systeme
        // n'a plus rien a apprendre, il le dit sans attendre le plafond.
        var (_, last) = PlayStable(4f);

        Assert.True(last.Ready);
        Assert.Equal("convergence", last.Reason);
    }

    [Fact]
    public void Le_feu_vert_arrive_bien_avant_le_plafond()
    {
        // L'interet du critere : sur un morceau lisible, on ne fait pas attendre le DJ
        // trente secondes pour rien.
        var gate = new KnowledgeGate();
        long at = -1;

        for (long t = 0; t < 30_000 && at < 0; t += Step)
            if (gate.Feed(t, 96f, 0.5f, 0.4f, 0.35f).Ready) at = t;

        Assert.InRange(at, 0, 5_000);
    }

    [Fact]
    public void Un_disque_qui_ne_se_stabilise_jamais_passe_par_le_plafond()
    {
        // LE CAS QUI JUSTIFIE LE PLAFOND. Un morceau dont rien ne se fixe — une intro
        // mouvante, un passage sans pulsation — ne doit pas bloquer indefiniment le feu
        // vert : un feu vert tardif ne sert a rien, la transition sera deja passee.
        var gate = new KnowledgeGate();
        var rnd = new Random(5);
        var last = Readiness.None;

        for (long t = 0; t < 40_000; t += Step)
            last = gate.Feed(t, null,
                (float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());

        Assert.True(last.Ready);
        Assert.Equal("plafond", last.Reason);
    }

    [Fact]
    public void Le_plafond_ne_tombe_pas_avant_son_heure()
    {
        var gate = new KnowledgeGate();
        var rnd = new Random(5);
        var last = Readiness.None;

        for (long t = 0; t < (long)(KnowledgeGate.CapSeconds * 1000) - 1_000; t += Step)
            last = gate.Feed(t, null,
                (float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());

        Assert.False(last.Ready);
        Assert.InRange(last.SecondsLeft, 0.5f, KnowledgeGate.CapSeconds);
    }

    [Fact]
    public void Un_tempo_absent_empeche_la_convergence()
    {
        // Ne rien dire plutot que dire faux : tant que le tempo n'est pas publie, on ne
        // peut pas pretendre connaitre le disque, quand bien meme sa couleur serait fixe.
        var gate = new KnowledgeGate();
        var last = Readiness.None;

        for (long t = 0; t < 10_000; t += Step)
            last = gate.Feed(t, null, 0.5f, 0.4f, 0.35f);

        Assert.False(last.Ready);
    }

    [Fact]
    public void L_avancement_progresse_et_ne_recule_pas_apres_le_feu_vert()
    {
        // Il sert a etre affiche sur le telephone : une jauge qui reculerait ferait douter
        // de ce qu'elle mesure.
        var gate = new KnowledgeGate();
        var seen = new List<float>();

        for (long t = 0; t < 8_000; t += Step)
            seen.Add(gate.Feed(t, 96f, 0.5f, 0.4f, 0.35f).Progress);

        Assert.True(seen[^1] >= seen[0]);
        Assert.Equal(1f, seen[^1]);
    }

    [Fact]
    public void Un_nouveau_disque_remet_tout_a_zero()
    {
        var (gate, _) = PlayStable(4f);
        Assert.True(gate.Current.Ready);

        gate.Reset();

        Assert.False(gate.Current.Ready);
        Assert.Equal(0f, gate.Current.Progress);
    }
}
