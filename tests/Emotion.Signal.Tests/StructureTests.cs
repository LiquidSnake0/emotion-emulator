using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class BeatGridTests
{
    private const float BeatMs = 690f;   // 87 BPM, le coeur du repertoire
    private const long Step = 21;        // une fenetre d'analyse

    /// <summary>
    /// Joue un motif de batterie regulier et rend les instants de debut de mesure.
    /// </summary>
    private static (BeatGrid Grid, List<long> BarStarts) Play(
        int[] kickOn, int[] clapOn, int bars = 24,
        bool chordOnOne = false, int breakEvery = 0)
    {
        var grid = new BeatGrid();
        var starts = new List<long>();
        var beat = 0;
        var total = bars * 4;

        for (long t = 0; beat < total; t += Step)
        {
            // Meme ordre qu'en production : on ferme le temps precedent avant de
            // marquer ce qui tombe dans le nouveau.
            grid.Advance(t, 87f);
            if (grid.BarStart) starts.Add(t);

            if (beat * BeatMs <= t)
            {
                var b = beat % 4;
                if (kickOn.Contains(b)) { grid.Sync(t); grid.MarkKick(t); }
                if (clapOn.Contains(b)) grid.MarkClap(t);
                if (chordOnOne && b == 0) grid.MarkChange(t);
                if (breakEvery > 0 && b == 0 && (beat / 4) % breakEvery == 0)
                    grid.AlignPhrase(t);
                beat++;
            }
        }

        return (grid, starts);
    }

    private static string Dump(BeatGrid g) =>
        string.Join(" ", g.Scores.Select(v => v.ToString("F1")));

    /// <summary>Ecart d'un instant au « 1 » musical le plus proche, en millisecondes.</summary>
    private static float OffBy(long tMs)
    {
        const float barMs = BeatMs * 4f;
        var m = tMs % barMs;
        return MathF.Min(m, barMs - m);
    }

    [Fact]
    public void Le_backbeat_seul_est_ambigu_et_la_grille_le_reconnait()
    {
        // Kick sur 1 et 3, clap sur 2 et 4 : le motif le plus repandu de la musique
        // populaire, et il est <b>symetrique par decalage de deux temps</b>. Le 1 et le 3
        // y sont litteralement indiscernables — l'information n'existe pas dans le
        // signal, aucun algorithme ne peut l'en tirer.
        //
        // Ce que ce test verifie n'est donc pas que la grille trouve le temps fort, mais
        // qu'elle <b>refuse d'en inventer un</b>. Tirer a pile ou face donnerait raison
        // une fois sur deux et decalerait toute la structure l'autre fois.
        var (grid, _) = Play(kickOn: [0, 2], clapOn: [1, 3]);

        Assert.False(grid.Locked, "aucune preuve ne distingue le 1 du 3 dans ce motif");
        Assert.Equal(-1, grid.Beat);
    }

    [Fact]
    public void Le_changement_d_accord_tranche_l_ambiguite()
    {
        // Il faut un indice de plus longue portee que la batterie. L'harmonie en est un :
        // un accord change sur le temps fort, presque jamais ailleurs.
        var (grid, starts) = Play(kickOn: [0, 2], clapOn: [1, 3], chordOnOne: true);

        Assert.True(grid.Locked, "le changement d'accord doit suffire a trancher");
        var late = starts.Skip(starts.Count / 2).ToList();
        Assert.NotEmpty(late);
        Assert.All(late, t => Assert.True(OffBy(t) < 120f,
            $"debut de mesure a {OffBy(t):F0} ms du « 1 » musical"));
    }

    [Fact]
    public void Une_rupture_de_section_tranche_aussi()
    {
        // Meme service, rendu par la structure plutot que par l'harmonie : une section ne
        // commence jamais au milieu d'une mesure.
        var (grid, starts) = Play(kickOn: [0, 2], clapOn: [1, 3], breakEvery: 8);

        Assert.True(grid.Locked, $"conf={grid.Confidence:F3} scores={Dump(grid)}");
        Assert.All(starts.Skip(starts.Count / 2), t => Assert.True(OffBy(t) < 120f, $"ecart {OffBy(t):F0} ms"));
    }

    [Fact]
    public void Un_kick_sur_les_quatre_temps_ne_noie_pas_le_reste()
    {
        // Quand le kick tombe partout il n'apprend rien — et il ne doit surtout pas
        // ecraser les indices qui, eux, discriminent : il ajoute la meme voix aux quatre
        // hypotheses, ce qui laisse leur ecart intact. Une somme naive de preuves
        // echouerait ici.
        var (grid, starts) = Play(kickOn: [0, 1, 2, 3], clapOn: [1, 3], chordOnOne: true);

        Assert.True(grid.Locked, $"conf={grid.Confidence:F3} scores={Dump(grid)}");
        Assert.All(starts.Skip(starts.Count / 2), t => Assert.True(OffBy(t) < 120f, $"ecart {OffBy(t):F0} ms"));
    }

    [Fact]
    public void Sans_aucun_indice_la_grille_ne_pretend_rien()
    {
        // Ne rien dire plutot que dire faux : sur un signal sans frappe, la confiance
        // reste nulle et le rang du temps vaut -1. Un visuel qui recevrait un « 1 »
        // invente calerait toute sa structure a cote.
        var grid = new BeatGrid();
        for (long t = 0; t < 20_000; t += Step) grid.Advance(t, 87f);

        Assert.False(grid.Locked);
        Assert.Equal(-1, grid.Beat);
    }

    [Fact]
    public void La_mesure_avance_et_la_phrase_boucle()
    {
        var (grid, starts) = Play(kickOn: [0, 2], clapOn: [1, 3], bars: 24, chordOnOne: true);

        Assert.True(starts.Count > 15, $"{starts.Count} mesures comptees");
        Assert.InRange(grid.Bar, 0, Structure.PhraseBars - 1);
    }

    [Fact]
    public void La_grille_absorbe_une_frappe_isolee_qui_tombe_a_cote()
    {
        // Une detection egaree ne doit deplacer la grille que d'un cinquieme de l'ecart.
        // Se caler entierement sur chaque frappe reviendrait a n'avoir aucune grille — on
        // retomberait sur une phase dont l'origine est le dernier coup entendu.
        var grid = new BeatGrid();
        for (long t = 0; t < 8_000; t += Step)
        {
            if (t % 690 < Step) grid.Sync(t);
            grid.Advance(t, 87f);
        }

        grid.Advance(9_000, 87f);
        var before = grid.Phase;
        grid.Sync(9_000 + 300);          // franchement hors grille
        grid.Advance(9_000, 87f);

        // Ecart circulaire : 0,04 et 0,95 sont voisins, pas opposes.
        var moved = MathF.Abs(grid.Phase - before);
        if (moved > 0.5f) moved = 1f - moved;
        Assert.InRange(moved, 0f, 0.25f);
    }
}

public class ArcDetectorTests
{
    private static void Beat(ArcDetector arc, float bass, float bright, float busy)
    {
        // Une trentaine de fenetres par temps, comme en vrai.
        for (var i = 0; i < 33; i++) arc.Feed(bass, bright, busy);
        arc.Advance();
    }

    [Fact]
    public void Une_montee_fait_monter_la_tension()
    {
        // Les trois pentes d'une montee de huit mesures : les aigus s'ouvrent, les graves
        // se retirent, il se passe de plus en plus de choses.
        var arc = new ArcDetector();
        for (var i = 0; i < 16; i++) Beat(arc, 0.7f, 0.25f, 0.3f);   // etat stable

        var calm = arc.Buildup;
        for (var i = 0; i < 32; i++)
        {
            var p = i / 31f;
            Beat(arc, 0.7f - 0.55f * p, 0.25f + 0.5f * p, 0.3f + 0.45f * p);
        }

        Assert.True(arc.Buildup > 0.4f, $"tension attendue en montee, obtenu {arc.Buildup:F2}");
        Assert.True(arc.Buildup > calm + 0.3f);
    }

    [Fact]
    public void Un_passage_stable_ne_tend_rien_et_ne_casse_rien()
    {
        // Le controle qui compte le plus : un morceau qui tourne ne doit produire ni
        // tension ni rupture. Un detecteur qui declenche sur du regulier declenche sur
        // tout, et n'apprend donc rien a personne.
        var arc = new ArcDetector();
        var drops = 0;
        for (var i = 0; i < 64; i++)
        {
            Beat(arc, 0.6f + (i % 4 == 0 ? 0.15f : 0f), 0.4f, 0.35f);
            if (arc.Drop) drops++;
        }

        Assert.Equal(0, drops);
        Assert.True(arc.Buildup < 0.25f, $"tension parasite {arc.Buildup:F2}");
    }

    [Fact]
    public void La_rupture_demande_une_tension_prealable()
    {
        // Un retour de graves apres une simple respiration d'une mesure n'est pas un
        // drop. Sans cette condition, le mot ne voudrait plus rien dire.
        var arc = new ArcDetector();
        for (var i = 0; i < 20; i++) Beat(arc, 0.6f, 0.4f, 0.35f);
        for (var i = 0; i < 4; i++) Beat(arc, 0.1f, 0.4f, 0.35f);   // respiration courte

        var drops = 0;
        for (var i = 0; i < 4; i++) { Beat(arc, 0.9f, 0.4f, 0.35f); if (arc.Drop) drops++; }

        Assert.Equal(0, drops);
    }

    [Fact]
    public void Une_montee_suivie_du_retour_des_graves_donne_une_rupture()
    {
        var arc = new ArcDetector();
        for (var i = 0; i < 16; i++) Beat(arc, 0.7f, 0.25f, 0.3f);
        for (var i = 0; i < 32; i++)
        {
            var p = i / 31f;
            Beat(arc, 0.7f - 0.6f * p, 0.25f + 0.55f * p, 0.3f + 0.5f * p);
        }

        var drops = 0;
        for (var i = 0; i < 8; i++) { Beat(arc, 0.95f, 0.45f, 0.5f); if (arc.Drop) drops++; }

        Assert.Equal(1, drops);   // une seule fois : le repos empeche de la rejouer
    }
}
