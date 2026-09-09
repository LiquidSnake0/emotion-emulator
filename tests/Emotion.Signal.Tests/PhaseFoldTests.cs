using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// Le repli d'energie, qui donne desormais sa phase a la grille.
///
/// Il existe parce que la grille tenait la sienne des seules frappes detectees, et que
/// cette phase etait AU NIVEAU DU HASARD : confrontee a une verite terrain exterieure sur
/// dix morceaux du bac, 0,252 temps d'ecart quand un tirage au sort en donne 0,25.
/// </summary>
public class PhaseFoldTests
{
    private const float BeatMs = 690f;
    private const long Step = 21;

    /// <summary>
    /// Joue une energie periodique et rend le repli. Le pic tombe a <paramref name="ou"/>
    /// dans le temps, entre 0 et 1.
    /// </summary>
    private static PhaseFold Jouer(float ou, int temps = 200, float periodeMs = BeatMs,
                                   float fond = 0.05f)
    {
        var repli = new PhaseFold();
        var duree = (long)(temps * periodeMs);
        for (long t = 0; t < duree; t += Step)
        {
            // La phase reelle de ce signal, et l'energie qu'on y met : une bosse etroite
            // au bon endroit, un fond partout.
            var phase = (t % (long)periodeMs) / periodeMs;
            var d = MathF.Abs(phase - ou);
            if (d > 0.5f) d = 1f - d;
            var energie = fond + (d < 0.03f ? 1f : 0f);
            repli.Feed(t, energie, 60_000f / periodeMs);
        }
        return repli;
    }

    [Fact]
    public void Il_trouve_ou_tombe_le_temps()
    {
        // On demande la phase a l'instant qui suit immediatement la derniere fenetre. Le
        // repli rend « depuis combien de temps le dernier temps », donc a un pic pose a
        // 0,25 et une derniere fenetre proche de la fin du cycle, l'ecart doit valoir la
        // distance entre les deux.
        foreach (var ou in new[] { 0.1f, 0.35f, 0.6f, 0.85f })
        {
            var repli = Jouer(ou);
            Assert.True(repli.Relief > 0.3f,
                $"pic a {ou} : relief {repli.Relief:F2}, le repli ne voit rien");
            // La phase interne du repli au pic doit coincider avec `ou`, a une case pres.
            var ecart = MathF.Abs(repli.Phase - ou);
            if (ecart > 0.5f) ecart = 1f - ecart;
            Assert.True(ecart < 2f / PhaseFold.Cases,
                $"pic a {ou} : repli a {repli.Phase:F3}, ecart {ecart:F3}");
        }
    }

    /// <summary>
    /// Une energie sans periode ne doit designer aucun temps. C'est la garde qui empeche
    /// une nappe ou une intro de deplacer une grille acquise : le relief pondere la
    /// correction, et un profil etale ne doit rien peser.
    /// </summary>
    [Fact]
    public void Sans_periode_il_ne_dit_rien()
    {
        var repli = new PhaseFold();
        var alea = new Random(12345);
        for (long t = 0; t < 200 * (long)BeatMs; t += Step)
            repli.Feed(t, (float)alea.NextDouble(), 60_000f / BeatMs);

        Assert.True(repli.Relief < 0.25f,
            $"relief {repli.Relief:F2} sur du bruit : il croit voir un temps");
    }

    /// <summary>
    /// IL RATTRAPE UNE PERIODE FAUSSE, ET C'EST TOUTE SA RAISON D'ETRE MULTIPLE.
    ///
    /// Le repli est d'une sensibilite qu'on ne devine pas : deux pour cent d'erreur de
    /// periode suffisent a le ramener au hasard, parce que sur quarante-huit temps de
    /// memoire cela fait un temps entier de derive. Or le tempo du moteur est faux de deux
    /// a cinq pour cent sur plusieurs morceaux du bac. Sans les candidats, la premiere
    /// version mesurait 165 ms d'ecart pour un hasard de 182 — c'est-a-dire rien.
    /// </summary>
    [Fact]
    public void Il_rattrape_une_periode_annoncee_fausse()
    {
        // Le signal bat a 690 ms ; on annonce 703, soit deux pour cent de trop.
        var repli = new PhaseFold();
        const float annonce = BeatMs * 1.019f;
        var duree = (long)(200 * BeatMs);
        for (long t = 0; t < duree; t += Step)
        {
            var phase = (t % (long)BeatMs) / BeatMs;
            var d = MathF.Abs(phase - 0.25f);
            if (d > 0.5f) d = 1f - d;
            repli.Feed(t, 0.05f + (d < 0.03f ? 1f : 0f), 60_000f / annonce);
        }

        Assert.True(repli.Relief > 0.3f,
            $"relief {repli.Relief:F2} : la periode fausse a etale le profil");
        // Le candidat retenu doit corriger vers le bas, puisqu'on a annonce trop long.
        Assert.True(repli.Facteur < 1f,
            $"facteur {repli.Facteur:F3} : il n'a pas cherche plus court");
    }

    [Fact]
    public void Il_ne_dit_rien_avant_d_avoir_entendu_quatre_temps()
    {
        var repli = new PhaseFold();
        for (long t = 0; t < 2 * (long)BeatMs; t += Step)
            repli.Feed(t, 1f, 60_000f / BeatMs);

        Assert.Equal(0f, repli.Relief);
    }
}

/// <summary>
/// L'enveloppe d'une source : ce qui separe une corde pincee d'un souffle.
///
/// Aucune grandeur ne le disait. Les deux produisent le meme niveau moyen et la meme
/// hauteur, et le rendu leur donnait donc le meme mouvement.
/// </summary>
public class SourceEnvelopeTests
{
    private const float Frame = 0.0213f;      // une fenetre d'analyse

    /// <summary>Joue un motif et rend l'enveloppe mesuree.</summary>
    private static (float Pique, float Tenue) Jouer(Func<int, float> niveau, int fenetres = 400)
    {
        var e = new SourceEnvelope(1, Frame);
        for (var i = 0; i < fenetres; i++) e.Feed(0, niveau(i));
        return (e.Pique(0), e.Tenue(0));
    }

    /// <summary>Un souffle : il s'installe et ne frappe jamais.</summary>
    [Fact]
    public void Un_continu_ne_pique_pas_et_tient()
    {
        var (pique, tenue) = Jouer(_ => 0.7f);
        Assert.True(pique < 0.15f, $"pique {pique:F2} sur un niveau constant");
        Assert.True(tenue > 0.9f, $"tenue {tenue:F2} sur un niveau constant");
    }

    /// <summary>
    /// Une corde pincee : elle monte en une fenetre et meurt avant la suivante.
    /// </summary>
    [Fact]
    public void Un_pince_pique_et_ne_tient_pas()
    {
        // Une note tous les trente-deux fenetres, soit environ deux tiers de seconde.
        var (pique, tenue) = Jouer(i =>
        {
            var depuis = i % 32;
            return depuis == 0 ? 1f : MathF.Max(0f, 1f - depuis * 0.4f);
        });
        Assert.True(pique > 0.5f, $"pique {pique:F2} sur des notes pincees");
        Assert.True(tenue < 0.45f, $"tenue {tenue:F2} sur des notes pincees");
    }

    /// <summary>
    /// UN PIANO : IL PIQUE ET IL TIENT UN PEU. C'est le cas qui prouve que les deux
    /// grandeurs sont independantes — « une frappe suivie d'une onde courte ou longue »,
    /// ou la longueur de l'onde EST la tenue.
    /// </summary>
    [Fact]
    public void Un_frappe_qui_resonne_pique_ET_tient()
    {
        var (pique, tenue) = Jouer(i =>
        {
            var depuis = i % 32;
            return depuis == 0 ? 1f : MathF.Exp(-depuis * 0.06f);
        });
        Assert.True(pique > 0.5f, $"pique {pique:F2} sur un piano");
        Assert.True(tenue > 0.45f, $"tenue {tenue:F2} sur un piano — il resonne pourtant");
    }

    /// <summary>Le paquet transporte les deux, sans les melanger.</summary>
    [Fact]
    public void Le_paquet_transporte_l_enveloppe()
    {
        var p = new GpuPacket();
        p.WriteEnvelope(2, 0.75f, 0.25f);
        p.WriteEnvelope(3, 0.25f, 0.75f);

        var (a, b) = p.ReadEnvelope(2);
        Assert.InRange(a / 255f, 0.74f, 0.76f);
        Assert.InRange(b / 255f, 0.24f, 0.26f);

        var (c, d) = p.ReadEnvelope(3);
        Assert.InRange(c / 255f, 0.24f, 0.26f);
        Assert.InRange(d / 255f, 0.74f, 0.76f);

        // Les enveloppes vivent hors du mot de la source : ecrire l'une ne doit pas
        // deplacer l'autre, ni toucher au mot lui-meme.
        Assert.Equal((byte)0, p.ReadSource(2).Level);
    }
}

/// <summary>
/// Une absence n'est pas un changement.
///
/// « Le seul changement qui justifierait de recheck le beat est un changement, pas un mute
/// du kick. » La regle vaut pour toutes les sources : un violon qui se tait reste un
/// violon, et le rendu ne doit pas lui donner le geste d'un souffle a son retour.
/// </summary>
public class RetraitTests
{
    private const float Frame = 0.0213f;

    /// <summary>
    /// LE SILENCE ENTRE DEUX NOTES DOIT CONTINUER DE COMPTER, et c'est ce qui rend la
    /// distinction delicate : c'est lui qui fait la tenue d'un pizzicato. Une premiere
    /// version gelait des le premier echantillon silencieux et mesurait donc un pizzicato
    /// comme un souffle.
    /// </summary>
    [Fact]
    public void Le_silence_entre_deux_notes_compte_toujours()
    {
        var e = new SourceEnvelope(1, Frame);
        for (var i = 0; i < 400; i++)
        {
            var depuis = i % 32;                       // une note tous les deux tiers de seconde
            e.Feed(0, depuis == 0 ? 1f : MathF.Max(0f, 1f - depuis * 0.5f));
        }
        Assert.True(e.Tenue(0) < 0.45f,
            $"tenue {e.Tenue(0):F2} : le blanc entre les notes n'a pas ete compte");
        Assert.True(e.Pique(0) > 0.5f, $"pique {e.Pique(0):F2}");
    }

    /// <summary>
    /// Une source qui quitte l'arrangement garde ce qu'elle etait. C'est le creux du
    /// morceau, celui ou il ne reste qu'une melodie.
    /// </summary>
    [Fact]
    public void Un_retrait_ne_fait_pas_oublier_ce_qu_on_savait()
    {
        var e = new SourceEnvelope(1, Frame);
        for (var i = 0; i < 400; i++)
        {
            var depuis = i % 32;
            e.Feed(0, depuis == 0 ? 1f : MathF.Max(0f, 1f - depuis * 0.5f));
        }
        // ON TESTE LA PROPRIETE, PAS LE CHIFFRE. Le pique oscille a l'interieur du cycle
        // d'une note — haut juste apres l'attaque, plus bas entre deux — donc comparer une
        // valeur prise a un instant quelconque du cycle a celle gelee ne veut rien dire.
        // Ce qui compte est que la source reste RECONNUE comme pincee.

        // Huit secondes de creux, soit bien plus que la fenetre d'observation.
        for (var i = 0; i < (int)(8f / Frame); i++) e.Feed(0, 0f);

        Assert.True(e.Muet(0) > SourceEnvelope.RetraitS, "le retrait n'a pas ete constate");
        Assert.True(e.Pique(0) > 0.5f,
            $"pique {e.Pique(0):F2} apres huit secondes de creux : elle a oublie qu'elle pincait");
        Assert.True(e.Tenue(0) < 0.45f,
            $"tenue {e.Tenue(0):F2} apres huit secondes de creux : elle se croit continue");
    }
}

/// <summary>
/// Le role d'une source dans l'orchestre.
///
/// « Le mec qui fait le tambour joue le metronome pour les autres, celui au saxophone est
/// charge de jouer les memes notes mais a des instants precis. »
/// </summary>
public class SourceRolesTests
{
    private const float BeatMs = 690f;
    private const long Step = 21;
    private const float Bpm = 60_000f / BeatMs;

    /// <summary>
    /// Joue un motif : pour chaque source, a quelles fractions de temps elle frappe.
    /// Une liste vide = elle ne frappe jamais.
    /// </summary>
    private static SourceRoles Jouer(float[][] places, int temps = 120)
    {
        var roles = new SourceRoles(places.Length);
        var frappes = new bool[places.Length];
        var precedent = new float[places.Length];
        for (var i = 0; i < places.Length; i++) precedent[i] = -1f;

        for (long t = 0; t < temps * (long)BeatMs; t += Step)
        {
            var phase = (t % (long)BeatMs) / BeatMs;
            var tour = t / (long)BeatMs;
            for (var s = 0; s < places.Length; s++)
            {
                frappes[s] = false;
                foreach (var p in places[s])
                {
                    // Une frappe par tour et par place : on tire quand la phase la franchit.
                    var cle = tour * 8 + (int)(p * 8);
                    if (phase >= p && phase < p + Step / BeatMs && precedent[s] != cle)
                    {
                        frappes[s] = true;
                        precedent[s] = cle;
                    }
                }
            }
            roles.Feed(t, Bpm, frappes);
        }
        return roles;
    }

    [Fact]
    public void Celle_qui_marque_chaque_temps_tient_le_metronome()
    {
        var r = Jouer([[0.02f]]);
        Assert.Equal(SourceRoles.Metronome, r.Role(0));
        Assert.InRange(r.Place(0), 0f, 0.12f);
    }

    /// <summary>
    /// Celle qui ne marque qu'un temps sur quatre ponctue. LA DIFFERENCE N'EST PAS DANS LA
    /// REGULARITE — les deux sont parfaitement regulieres — MAIS DANS LA DENSITE : on ne
    /// peut pas predire le temps suivant a partir d'une source qui n'en marque qu'un sur
    /// quatre.
    /// </summary>
    [Fact]
    public void Celle_qui_marque_une_position_precise_ponctue()
    {
        // Un accord sur le dernier quart du temps, une fois sur quatre.
        var roles = new SourceRoles(1);
        var frappes = new bool[1];
        var precedent = -1L;
        for (long t = 0; t < 200 * (long)BeatMs; t += Step)
        {
            var tour = t / (long)BeatMs;
            var phase = (t % (long)BeatMs) / BeatMs;
            frappes[0] = tour % 4 == 0 && phase >= 0.75f && phase < 0.75f + Step / BeatMs
                         && precedent != tour;
            if (frappes[0]) precedent = tour;
            roles.Feed(t, Bpm, frappes);
        }
        Assert.Equal(SourceRoles.Ponctuel, roles.Role(0));
        Assert.InRange(roles.Place(0), 0.68f, 0.88f);
    }

    /// <summary>
    /// Celle qui frappe n'importe quand est continue. Elle ne temoigne de rien, et c'est
    /// pour cela que la grille ne doit pas s'appuyer sur elle.
    /// </summary>
    [Fact]
    public void Celle_qui_frappe_n_importe_quand_est_continue()
    {
        var roles = new SourceRoles(1);
        var frappes = new bool[1];
        var alea = new Random(7);
        for (long t = 0; t < 200 * (long)BeatMs; t += Step)
        {
            frappes[0] = alea.NextDouble() < 0.04;
            roles.Feed(t, Bpm, frappes);
        }
        Assert.Equal(SourceRoles.Continu, roles.Role(0));
    }

    /// <summary>Sans assez de frappes, on ne se prononce pas.</summary>
    [Fact]
    public void Sans_assez_de_frappes_on_ne_dit_rien()
    {
        var roles = new SourceRoles(1);
        var frappes = new bool[1];
        for (long t = 0; t < 20 * (long)BeatMs; t += Step)
        {
            frappes[0] = t % (long)BeatMs < Step && t > 15 * (long)BeatMs;
            roles.Feed(t, Bpm, frappes);
        }
        Assert.Equal(SourceRoles.Inconnu, roles.Role(0));
    }

    [Fact]
    public void Le_paquet_transporte_le_role_la_place_et_le_retrait()
    {
        var p = new GpuPacket();
        p.WriteRole(1, SourceRoles.Ponctuel, 0.75f, 3.5f);
        var (kind, place, away) = p.ReadRole(1);
        Assert.Equal(SourceRoles.Ponctuel, kind);
        Assert.InRange(place / 255f, 0.74f, 0.76f);
        Assert.InRange(away * GpuPacket.RoleAwayStep, 3.4f, 3.6f);

        // Les roles closent le paquet : ecrire le dernier ne doit rien deborder.
        p.WriteRole(GpuPacket.SourceSlots - 1, SourceRoles.Metronome, 1f, 15f);
        Assert.Equal(SourceRoles.Ponctuel, p.ReadRole(1).Kind);
    }
}
