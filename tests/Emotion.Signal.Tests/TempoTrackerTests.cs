using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class TempoTrackerTests
{
    // Les quatre derniers tests de ce fichier portaient sur le relais entre platines et
    // vivaient a part. Ils ont rejoint le code qu'ils exercent : le relais n'est pas un
    // mecanisme separe, c'est une facon d'amorcer cet estimateur-la.

    private const int Rate = 48_000;
    private const int Window = SpectrumAnalyzer.Window;
    private const float FrameMs = Window * 1000f / Rate;   // 21,33 ms

    /// <summary>
    /// Fabrique une enveloppe d'attaque : une decroissance exponentielle a chaque temps,
    /// ce que rend le registre du kick sur un morceau ordinaire.
    /// </summary>
    private static TempoTracker Play(float bpm, float seconds,
                                     Func<int, bool>? silence = null,
                                     Func<int, float>? jitter = null)
    {
        var t = new TempoTracker(Rate, Window);
        var beatMs = 60_000f / bpm;
        var frames = (int)(seconds * 1000f / FrameMs);

        for (var i = 0; i < frames; i++)
        {
            var tMs = i * FrameMs + (jitter?.Invoke(i) ?? 0f);
            var index = (int)(tMs / beatMs);
            var pos = (tMs % beatMs) / beatMs;
            var v = silence is not null && silence(index) ? 0.02f : MathF.Exp(-pos * 14f);
            t.Feed(v);
        }

        return t;
    }

    [Fact]
    public void Une_enveloppe_reguliere_donne_son_tempo()
    {
        var t = Play(87f, 20f);

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 84f, 90f);
    }

    [Fact]
    public void Une_frappe_sur_quatre_manquante_ne_change_rien()
    {
        // LE TEST QUI JUSTIFIE TOUT LE CHANGEMENT.
        //
        // L'ancienne methode votait sur les ecarts entre attaques consecutives : une
        // frappe manquee produit un ecart double, qui ne decrit aucun tempo reel. Une
        // autocorrelation ne demande aucune decision binaire — elle mesure a quel point
        // l'enveloppe ressemble a elle-meme decalee — et les temps restants continuent
        // de se ressembler exactement comme avant.
        var t = Play(87f, 20f, silence: i => i % 4 == 3);

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 84f, 90f);
    }

    [Fact]
    public void Un_jeu_imprecis_ne_disperse_pas_le_tempo()
    {
        // L'autre defaut de l'ancienne methode : elle regroupait par cases de dix
        // millisecondes, soit 1,4 % d'un temps. Une frappe jouee a la main en sort en
        // permanence, et les voix se dispersaient entre deux cases voisines sans qu'aucune
        // n'atteigne le tiers requis. Sur un vrai set, le tempo n'etait publie que sur
        // 4 % des fenetres.
        var rnd = new Random(1203);
        var t = Play(87f, 20f, jitter: _ => (float)(rnd.NextDouble() * 24 - 12));

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 83f, 91f);
    }

    [Fact]
    public void Un_signal_sans_pulsation_ne_donne_pas_de_tempo()
    {
        // Ne rien dire plutot que dire faux. Une nappe, un souffle, un blanc entre deux
        // disques n'ont pas de tempo, et en inventer un ferait battre tout le visuel sur
        // une pulsation qui n'existe pas.
        var t = new TempoTracker(Rate, Window);
        var rnd = new Random(7);
        for (var i = 0; i < 900; i++) t.Feed((float)rnd.NextDouble() * 0.3f);

        Assert.True(t.Bpm is null || t.Confidence < 0.35f,
            $"tempo invente : {t.Bpm} a {t.Confidence:F2} de confiance");
    }

    [Fact]
    public void Le_relais_donne_un_tempo_tout_de_suite()
    {
        // Le but du passage de relais : ne pas laisser le master chercher pendant le
        // passage le plus visible du set. Une autocorrelation partie de rien demande
        // plusieurs secondes d'observation ; celle-ci repond des la premiere image.
        var t = new TempoTracker(Rate, Window);
        Assert.Null(t.Bpm);

        t.Adopt(87f, 10_000);

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 84f, 90f);
    }

    [Fact]
    public void Un_tempo_aberrant_n_est_pas_repris()
    {
        var t = new TempoTracker(Rate, Window);
        t.Adopt(15f, 0);
        Assert.Null(t.Bpm);
    }

    [Fact]
    public void Le_relais_recale_aussi_la_phase()
    {
        // Sans ancre, la phase resterait celle du morceau precedent et les figures a
        // l'echelle de la mesure tomberaient a cote pendant tout le debut du disque.
        var t = new TempoTracker(Rate, Window);
        t.Adopt(87f, 5_000);

        var phase = t.Phase(5_000);
        Assert.NotNull(phase);
        Assert.InRange(phase!.Value, 0f, 0.02f);
    }

    [Fact]
    public void Le_master_reprend_la_main_sur_le_tempo_repris()
    {
        // Le relais amorce, il ne verrouille pas. Pendant le beatmatch le pitch a bouge —
        // c'est le but du geste — donc ce que le cue a entendu n'est deja plus ce qui sort
        // en salle. Le signal reel doit reprendre la main.
        var t = new TempoTracker(Rate, Window);
        t.Adopt(87f, 0);

        // Le disque sort en fait a 96 BPM, pitche pendant le calage.
        var beatMs = 60_000f / 96f;
        for (var i = 0; i < 1400; i++)
        {
            var tMs = i * FrameMs;
            t.Feed(MathF.Exp(-((tMs % beatMs) / beatMs) * 14f));
        }

        Assert.NotNull(t.Bpm);
        Assert.InRange(t.Bpm!.Value, 93f, 99f);
    }
}
