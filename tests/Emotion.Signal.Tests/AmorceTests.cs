using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// LA FICHE DIT OU CHERCHER, ELLE NE DIT PAS QUOI TROUVER.
///
/// Le crate est la premiere source d'information du systeme, et son tempo est le point de
/// depart de l'analyse. Mais un BPM stocke est faux des la premiere seconde : un vinyle se
/// joue a plus ou moins huit pour cent au fader, jusqu'a seize. Toute la difficulte tient
/// donc dans une distinction que ces tests fixent — amorcer n'est pas verrouiller.
/// </summary>
public class AmorceTests
{
    private const int Rate = 48_000;
    private static readonly float FrameMs = SpectrumAnalyzer.Window * 1000f / Rate;

    /// <summary>
    /// Nourrit le suiveur d'une enveloppe qui pulse a un tempo donne, assez longtemps pour
    /// qu'il conclue.
    /// </summary>
    private static TempoTracker Pulser(float bpm, float prefere, int secondes = 20)
    {
        var t = new TempoTracker(Rate, SpectrumAnalyzer.Window);
        t.Preferer(prefere);

        var periode = 60_000f / bpm / FrameMs;         // en fenetres
        var images = (int)(secondes * 1000f / FrameMs);
        for (var i = 0; i < images; i++)
        {
            // Une impulsion nette a chaque temps, du silence entre : le cas le plus simple
            // ou l'on connait la reponse.
            var phase = i % periode;
            t.Feed(phase < 1f ? 1f : 0.02f);
        }
        return t;
    }

    /// <summary>
    /// Le cas qui a motive tout ceci. Un morceau a 63,5 BPM — le plus lent de l'album du
    /// crate — tombe sous la borne basse d'une preference centree sur 90. Amorce sur sa
    /// fiche, il est retrouve.
    /// </summary>
    [Fact]
    public void Un_morceau_lent_est_retrouve_grace_a_sa_fiche()
    {
        var sans = Pulser(63.5f, prefere: 90f);
        var avec = Pulser(63.5f, prefere: 63.5f);

        Assert.NotNull(avec.Bpm);
        Assert.InRange(avec.Bpm!.Value, 61f, 66f);

        // Sans la fiche, le suiveur n'a aucune raison de choisir ce niveau-la plutot qu'un
        // de ses multiples, qui tombent, eux, dans sa zone preferee.
        var justeSansFiche = sans.Bpm is { } b && MathF.Abs(b - 63.5f) < 2f;
        Assert.False(justeSansFiche && avec.Bpm is null, "l'amorce ne doit jamais nuire");
    }

    /// <summary>
    /// AMORCER N'EST PAS VERROUILLER, et c'est la garantie qui rend la chose acceptable.
    ///
    /// Le DJ pousse le fader : le disque part a 95 alors que sa fiche annonce 87. Le
    /// systeme doit suivre le disque, pas la fiche. Sans quoi la projection montrerait un
    /// tempo que personne n'entend.
    /// </summary>
    [Fact]
    public void Un_disque_joue_plus_vite_que_sa_fiche_est_suivi_quand_meme()
    {
        var t = Pulser(95f, prefere: 87f);

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 92f, 98f);
    }

    /// <summary>
    /// Une fiche absurde ne doit pas pouvoir emmener l'analyse hors du monde. On garde
    /// alors la preference en cours plutot que d'en fabriquer une fausse.
    /// </summary>
    [Fact]
    public void Une_fiche_hors_bornes_est_ignoree()
    {
        var t = new TempoTracker(Rate, SpectrumAnalyzer.Window);
        t.Preferer(87f);
        t.Preferer(5f);          // absurde
        t.Preferer(4000f);       // absurde

        // La preference de 87 tient toujours : un morceau a 87 reste trouve.
        var periode = 60_000f / 87f / FrameMs;
        for (var i = 0; i < 1200; i++) t.Feed(i % periode < 1f ? 1f : 0.02f);

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 84f, 90f);
    }

    /// <summary>
    /// L'analyseur expose le meme geste, et c'est par la que la fiche du crate entre
    /// reellement — <see cref="SpectrumAnalyzer.Reprendre"/> l'appelle quand la memoire de
    /// piste rend ce qu'elle savait du disque.
    /// </summary>
    [Fact]
    public void L_analyseur_accepte_l_amorce_et_la_republie()
    {
        var a = new SpectrumAnalyzer(Rate);
        a.Amorcer(63.5f);
        Assert.NotNull(a.Reference.Expected);
        Assert.Equal(63.5f, a.Reference.Expected!.Value, 2);

        // Zero ne dit rien : on ne remplace pas une reference connue par du vide.
        a.Amorcer(0f);
        Assert.Equal(63.5f, a.Reference.Expected!.Value, 2);
    }
}
