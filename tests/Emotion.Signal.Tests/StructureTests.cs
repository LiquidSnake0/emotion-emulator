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
                    grid.MarkSection(t);
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
    public void La_mesure_avance_au_rythme_de_la_musique()
    {
        // La grille ne compte plus les phrases — SectionTracker les mesure. Elle ne rend
        // que le franchissement de mesure, et c'est deja ce qu'on lui demande de plus
        // difficile.
        var (_, starts) = Play(kickOn: [0, 2], clapOn: [1, 3], bars: 24, chordOnOne: true);

        Assert.True(starts.Count > 15, $"{starts.Count} mesures comptees");
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

/// <summary>
/// La phase du temps, telle qu'elle part vers l'unite de rendu.
///
/// Elle a ete ajoutee au contrat parce que le rendu n'avait rien d'utilisable pour
/// anticiper : il derivait sa cadence des drapeaux de frappe, qui sur un morceau du bac
/// tombent tous les 962 ms pour un temps de 688 — sept temps marques sur dix. Une horloge
/// ne verrouille pas sur un train troue.
/// </summary>
public class BeatPhaseTests
{
    private const float BeatMs = 690f;
    private const long Step = 21;

    /// <summary>
    /// ELLE EST PUBLIEE MEME QUAND LE « 1 » EST INCONNU, et c'est tout son interet.
    ///
    /// <see cref="VisualFrame.Phase"/> est une position dans la mesure de quatre temps :
    /// elle est nulle tant que le temps fort n'est pas identifie, ce qui sur un passage
    /// mesure du repertoire laisse le rendu sans reference quatre-vingt-six pour cent du
    /// temps. Savoir ou l'on en est du temps ne demande pourtant pas de savoir quel temps
    /// c'est.
    /// </summary>
    [Fact]
    public void La_phase_du_temps_avance_meme_sans_temps_fort()
    {
        var grid = new BeatGrid();
        var vues = new List<float>();

        // Aucune frappe, aucun vote : le « 1 » ne peut pas etre identifie.
        for (long t = 0; t < 4000; t += Step)
        {
            grid.Advance(t, 60_000f / BeatMs);
            vues.Add(grid.Phase);
        }

        Assert.Equal(-1, grid.Beat);              // le temps fort reste inconnu
        Assert.Contains(vues, p => p > 0.9f);     // la phase, elle, parcourt son tour
        Assert.Contains(vues, p => p < 0.1f);
    }

    /// <summary>
    /// Elle tourne a la cadence du tempo, et non a une autre.
    ///
    /// C'est la seule chose qui rende la prediction possible : une horloge de rendu qui
    /// s'y cale doit pouvoir en deduire quand tombe le temps suivant.
    /// </summary>
    [Fact]
    public void La_phase_du_temps_tourne_a_la_cadence_du_tempo()
    {
        var grid = new BeatGrid();
        var tours = 0;
        var precedente = 0f;

        const long duree = 30_000;
        for (long t = 0; t < duree; t += Step)
        {
            grid.Advance(t, 60_000f / BeatMs);
            if (grid.Phase < precedente - 0.5f) tours++;
            precedente = grid.Phase;
        }

        // Trente secondes a 690 ms le temps : quarante-trois tours, a un pres selon ou
        // l'echantillonnage tombe.
        var attendus = duree / BeatMs;
        Assert.InRange(tours, (int)attendus - 1, (int)attendus + 1);
    }

    /// <summary>
    /// Le paquet la transporte sans la perdre. Un octet donne un deux-cent-cinquante-
    /// sixieme de temps, soit 2,7 ms a 88 BPM — huit fois plus fin que le pas d'analyse
    /// de 21 ms qui la produit.
    /// </summary>
    [Fact]
    public void Le_paquet_transporte_la_phase_du_temps()
    {
        foreach (var phase in new[] { 0f, 0.25f, 0.5f, 0.75f, 0.999f })
        {
            var frame = new VisualFrame(0, 0.1f, new float[VisualFrame.BandCount],
                                        false, null, null,
                                        Structure: Structure.None with { BeatPhase = phase });
            var p = GpuPacket.From(frame, TrackContext.Silence, 0);
            Assert.InRange(p.BeatPhase / 255f, phase - 0.005f, phase + 0.005f);
        }
    }
}
