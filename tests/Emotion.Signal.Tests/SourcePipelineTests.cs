using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class SourcePipelineTests
{
    private static float[] Spectre(int seed)
    {
        var rng = new Random(seed);
        var s = new float[512];
        for (var i = 0; i < s.Length; i++) s[i] = (float)rng.NextDouble();
        return s;
    }

    /// <summary>
    /// LE TEST QUI JUSTIFIE TOUTE L'ARCHITECTURE.
    ///
    /// Six voies qui tournent ensemble doivent donner exactement ce que donneraient six
    /// voies qui tournent l'une apres l'autre. Si une seule grandeur etait partagee, les
    /// deux resultats divergeraient — c'etait le cas de l'ancien <c>hits</c>, un octet
    /// commun ou six voies posaient leur bit.
    /// </summary>
    [Fact]
    public void Le_parallele_donne_exactement_le_meme_resultat_que_le_sequentiel()
    {
        var a = new VoiceTracker(48_000, 1024);
        var b = new VoiceTracker(48_000, 1024);
        a.Pipeline.Force(parallel: false);
        b.Pipeline.Force(parallel: true);

        Voices last = default, other = default;
        for (var t = 0; t < 300; t++)
        {
            var s = Spectre(t);
            last = a.Feed(s);
            other = b.Feed(s);

            for (var r = 0; r < Voices.Registers; r++)
            {
                Assert.Equal(last.LevelAt(r), other.LevelAt(r), 6);
                Assert.Equal(last.PitchAt(r), other.PitchAt(r), 6);
            }

            Assert.Equal(last.Hits, other.Hits);
        }

        // Et le signal doit avoir reellement fait travailler les voies : un test qui passe
        // sur du silence ne prouve rien.
        Assert.True(last.LevelAt(0) > 0f);
    }

    /// <summary>
    /// Chaque source ecrit dans son mot a elle : ce qu'une voie ecrit ne doit jamais
    /// apparaitre dans le mot d'une autre.
    /// </summary>
    [Fact]
    public void Chaque_source_ecrit_dans_sa_propre_zone()
    {
        var p = new GpuPacket();

        for (var i = 0; i < GpuPacket.SourceCount; i++)
            p.WriteSource(
                i,
                new LaneState((i + 1) / 10f, (i + 1) / 20f, i % 2 == 0,
                              Confidence: (i + 1) / 8f, Brightness: (i + 1) / 9f),
                label: (byte)(i + 10));

        for (var i = 0; i < GpuPacket.SourceCount; i++)
        {
            var s = p.ReadSource(i);
            Assert.Equal((byte)Math.Clamp((i + 1) / 10f * 255f, 0f, 255f), s.Level);
            Assert.Equal((byte)Math.Clamp((i + 1) / 20f * 255f, 0f, 255f), s.Pitch);
            Assert.Equal(i % 2 == 0, s.Hit);

            // Le nom et l'empreinte voyagent dans le meme mot, sans attendre l'un l'autre.
            Assert.Equal((byte)(i + 10), s.Label);
            Assert.Equal((byte)Math.Clamp((i + 1) / 8f * 255f, 0f, 255f), s.Confidence);
            Assert.Equal((byte)Math.Clamp((i + 1) / 9f * 255f, 0f, 255f), s.Brightness);
        }
    }

    /// <summary>
    /// Six fils qui ecrivent leur propre source en meme temps ne doivent rien se perdre.
    /// L'ancienne disposition rangeait les six attaques dans un octet commun : ce test
    /// l'aurait fait tomber.
    /// </summary>
    [Fact]
    public void Six_ecritures_simultanees_ne_se_marchent_pas_dessus()
    {
        for (var essai = 0; essai < 200; essai++)
        {
            // Un tableau d'un element : l'indexeur rend une reference, donc les six fils
            // ecrivent bien dans le meme paquet et non dans six copies — sans quoi le test
            // ne prouverait rien.
            var p = new GpuPacket[1];

            System.Threading.Tasks.Parallel.For(0, GpuPacket.SourceCount,
                i => p[0].WriteSource(
                    i, new LaneState((i + 1) / 10f, (i + 1) / 20f, Hit: true),
                    label: (byte)(i + 1)));

            for (var i = 0; i < GpuPacket.SourceCount; i++)
            {
                var s = p[0].ReadSource(i);
                Assert.True(s.Hit, $"attaque perdue sur la source {i}");
                Assert.Equal((byte)(i + 1), s.Label);
            }
        }
    }
}

public class SourceIdentityTests
{
    /// <summary>
    /// Une source qui joue accumule de la confiance ; une source muette n'en accumule
    /// aucune. C'est ce qui permet aux six de murir a des rythmes differents sans jamais
    /// retenir le flux : la publication ne depend pas de la plus lente.
    /// </summary>
    [Fact]
    public void La_confiance_monte_au_rythme_de_ce_qui_joue()
    {
        var actif = new SourceIdentity();
        var muet = new SourceIdentity();

        var spectre = new float[64];
        for (var i = 0; i < spectre.Length; i++) spectre[i] = i == 20 ? 1f : 0.05f;

        var etapes = new List<float>();
        for (var t = 0; t < 400; t++)
        {
            actif.Feed(spectre, 0, 64, level: 0.8f);

            // Sous le seuil d'audibilite : cette source ne joue pas, elle n'a donc rien a
            // apprendre d'elle-meme.
            muet.Feed(spectre, 0, 64, level: 0.01f);

            if (t % 100 == 99) etapes.Add(actif.Confidence);
        }

        Assert.Equal(0f, muet.Confidence);
        Assert.True(actif.Confidence > 0.8f, $"confiance finale {actif.Confidence:F2}");

        // Et elle monte progressivement, image apres image, au lieu d'apparaitre d'un coup.
        for (var i = 1; i < etapes.Count; i++)
            Assert.True(etapes[i] >= etapes[i - 1], "la confiance doit croitre");
        Assert.True(etapes[0] < etapes[^1]);
    }

    /// <summary>
    /// Deux timbres qui se relaient dans la meme bande ne doivent pas produire une source
    /// sure d'elle : c'est precisement le cas que Selim decrivait — « piano et saxophone
    /// qui s'additionnent, ca donne un truc illisible ». Une bande partagee doit se
    /// declarer inconnue plutot que de recevoir un nom qui vaudra pour la moitie du temps.
    ///
    /// A NOTER, PARCE QUE LA PREMIERE VERSION DE CE TEST ETAIT FAUSSE. Elle nourrissait du
    /// bruit blanc en croyant fabriquer de l'instabilite, et la confiance montait a 0,74 —
    /// a juste titre : un souffle a un portrait tres stable, c'est une texture reconnaissable
    /// et non une bande partagee. Ce qui rend une bande illisible n'est pas le desordre,
    /// c'est l'<b>alternance entre deux portraits nets</b>.
    /// </summary>
    [Fact]
    public void Deux_timbres_qui_se_relaient_laissent_la_bande_inconnue()
    {
        var id = new SourceIdentity();
        var spectre = new float[64];

        for (var t = 0; t < 600; t++)
        {
            // Un coup une raie grave, un coup une raie aigue : deux instruments distincts
            // qui tombent dans le meme registre.
            var pic = t % 2 == 0 ? 6 : 56;
            for (var i = 0; i < spectre.Length; i++) spectre[i] = i == pic ? 1f : 0.02f;

            id.Feed(spectre, 0, 64, level: 0.8f);
        }

        Assert.True(id.Confidence < 0.4f,
                    $"confiance {id.Confidence:F2} : trop haute pour une bande que deux timbres se partagent");
    }
}

public class TempoReferenceTests
{
    /// <summary>
    /// Un disque conforme a sa fiche ne doit produire aucune derive : la reference sert a
    /// ne pas recalculer, et ne doit rien inventer.
    /// </summary>
    [Fact]
    public void Un_disque_conforme_ne_derive_pas()
    {
        var r = new TempoReference { Expected = 87.6f };
        for (var t = 0; t <= 60_000; t += 21) r.Feed(87.6f, t);

        Assert.True(MathF.Abs(r.Drift) < 0.01f, $"derive {r.Drift:F3}");
        Assert.Equal(0f, r.Visible);
    }

    /// <summary>
    /// UN BPM D'ECART, ET C'EST LE CHIFFRE QUI JUSTIFIE TOUTE LA CLASSE.
    ///
    /// 87,6 contre 88,6 fait 1,1 % : un ecart dont on jurerait qu'il ne se voit pas. Sur
    /// les seize temps du palier — onze secondes — la grille a deja glisse d'un sixieme de
    /// temps, et au bout d'une minute d'un temps entier. C'est exactement ce que Selim
    /// demande a voir.
    /// </summary>
    [Fact]
    public void Un_bpm_d_ecart_se_voit_en_moins_d_une_minute()
    {
        var r = new TempoReference { Expected = 87.6f };

        // Seize temps a 87,6 BPM : le palier.
        var seize = (long)(16 * 60_000 / 87.6f);
        for (long t = 0; t <= seize; t += 21) r.Feed(88.6f, t);
        var apresSeize = MathF.Abs(r.Drift);

        for (long t = seize; t <= 60_000; t += 21) r.Feed(88.6f, t);

        Assert.True(apresSeize > 0.1f, $"apres 16 temps : {apresSeize:F3} temps de decalage");
        Assert.True(MathF.Abs(r.Drift) > 0.9f, $"apres une minute : {r.Drift:F2} temps");
        Assert.Equal(1f, r.Visible);
    }

    /// <summary>
    /// Tant que le tempo n'est pas accroche, on n'accumule rien : une derive fabriquee sur
    /// une mesure absente serait une alerte inventee.
    /// </summary>
    [Fact]
    public void Sans_tempo_mesure_on_attend()
    {
        var r = new TempoReference { Expected = 87.6f };
        for (var t = 0; t <= 30_000; t += 21) r.Feed(null, t);

        Assert.Equal(0f, r.Drift);
    }
}

public class TempoAnnonceTests
{
    /// <summary>
    /// Un tempo qui derive doucement doit produire une suite d'annonces — « on est a
    /// 87,9 », puis « 88,5 » — et non une par fenetre.
    /// </summary>
    [Fact]
    public void Une_derive_lente_produit_une_suite_d_annonces()
    {
        var r = new TempoReference { Expected = 87.6f };
        var annonces = new List<float>();

        for (long t = 0; t <= 60_000; t += 21)
        {
            // Le plateau glisse de 87,6 a 89,6 en une minute.
            var bpm = 87.6f + 2f * (t / 60_000f);
            r.Feed(bpm, t);
            if (r.Announced) annonces.Add(r.Announcement);
        }

        // Deux BPM parcourus par pas de 0,68 : trois annonces, plus celle du depart.
        Assert.InRange(annonces.Count, 3, 5);
        for (var i = 1; i < annonces.Count; i++)
            Assert.True(annonces[i] > annonces[i - 1], "les annonces doivent suivre la derive");
    }

    /// <summary>
    /// Un tempo stable qui tremble d'un centieme n'annonce rien apres la premiere fois :
    /// une annonce par fenetre serait un clignotement, pas une information.
    /// </summary>
    [Fact]
    public void Un_tempo_stable_n_annonce_qu_une_fois()
    {
        var r = new TempoReference();
        var n = 0;
        var rng = new Random(3);

        for (long t = 0; t <= 60_000; t += 21)
        {
            r.Feed(87.6f + (float)(rng.NextDouble() - 0.5) * 0.4f, t);
            if (r.Announced) n++;
        }

        Assert.Equal(1, n);
    }
}

public class ConnaissanceTests
{
    /// <summary>
    /// Une source au timbre stable mais dont chaque note tombe ailleurs, dans une plage
    /// etroite. C'est ce a quoi ressemble un instrument reel : reconnaissable dans
    /// l'ensemble, jamais identique d'une image a l'autre.
    ///
    /// Le premier signal de ce test etait une note qui ne bougeait pas : la moyenne y
    /// convergeait des la trentieme observation, et les paliers rendaient tous le meme
    /// ecart. Un signal sans variation ne permet pas de mesurer une convergence.
    /// </summary>
    private static float[] Note(int t)
    {
        var s = new float[64];
        var pic = 16 + (int)(t * 2654435761u % 11);
        for (var i = 0; i < s.Length; i++) s[i] = i == pic ? 1f : 0.03f;
        return s;
    }

    /// <summary>
    /// LA PROPRIETE QUE SELIM DEMANDE, ENONCEE COMME UN TEST.
    ///
    /// Arreter le son a mi-parcours, le relancer et laisser tourner doit donner ce que
    /// donnerait une ecoute continue : la seconde moitie <b>corrige</b> ce que la premiere
    /// a etabli. Un systeme qui repartirait de zero a chaque lancement ne saurait jamais
    /// rien d'un disque qu'on ecoute par morceaux — c'est-a-dire de tous.
    /// </summary>
    [Fact]
    public void Deux_ecoutes_valent_une_ecoute_continue()
    {
        // Ecoute continue, mille images.
        var continu = new SourceIdentity();
        for (var t = 0; t < 1000; t++) continu.Feed(Note(t), 0, 64, 0.8f);

        // Meme matiere, coupee en deux, avec un rangement au milieu.
        var premiere = new SourceIdentity();
        for (var t = 0; t < 500; t++) premiere.Feed(Note(t), 0, 64, 0.8f);
        var range = premiere.Save();

        var seconde = new SourceIdentity();
        seconde.Load(range);
        for (var t = 500; t < 1000; t++) seconde.Feed(Note(t), 0, 64, 0.8f);

        Assert.Equal(continu.Observations, seconde.Observations);
        Assert.Equal(continu.Brightness, seconde.Brightness, 3);
        Assert.Equal(continu.Texture, seconde.Texture, 3);
        Assert.Equal(continu.Confidence, seconde.Confidence, 3);
    }

    [Fact]
    public void Une_reprise_en_sait_plus_qu_un_depart_de_zero()
    {
        var avecReprise = new SourceIdentity();
        var depuisZero = new SourceIdentity();

        var amorce = new SourceIdentity();
        for (var t = 0; t < 500; t++) amorce.Feed(Note(t), 0, 64, 0.8f);
        avecReprise.Load(amorce.Save());

        // Soixante images seulement : bien en dessous de ce qu'il faut pour murir seul,
        // ce qui est justement le cas ou la reprise doit se voir. Comparer apres deux
        // cents images ne prouverait rien — les deux auraient sature.
        for (var t = 500; t < 560; t++)
        {
            avecReprise.Feed(Note(t), 0, 64, 0.8f);
            depuisZero.Feed(Note(t), 0, 64, 0.8f);
        }

        Assert.True(avecReprise.Confidence > depuisZero.Confidence,
                    $"reprise {avecReprise.Confidence:F2} contre depart de zero {depuisZero.Confidence:F2}");
    }

    /// <summary>
    /// ECOUTER PLUS LONGTEMPS DOIT AMELIORER, ET CE N'ETAIT PAS ACQUIS.
    ///
    /// Avec un pas de correction fixe, le portrait flotte indefiniment : la millieme
    /// observation pese autant que la dixieme, donc la valeur oscille autour de la bonne
    /// sans s'y poser. Ce test verifie que l'ecart au portrait final se resserre a mesure
    /// qu'on ecoute — c'est cela, tendre vers les valeurs justes.
    /// </summary>
    [Fact]
    public void Le_portrait_se_resserre_a_mesure_qu_on_ecoute()
    {
        var id = new SourceIdentity();
        var releves = new List<float>();

        // Les releves se font tot, la ou la convergence se joue : passe quelques centaines
        // d'observations tout est deja pose, et comparer des ecarts de l'ordre de 1e-4
        // ne mesurerait plus que du bruit.
        // Alignes sur le cycle du signal : des jalons pris au milieu d'un cycle
        // compareraient deux phases differentes et non deux etats de convergence. Un
        // premier essai le faisait, et deux jalons y rendaient exactement le meme ecart.
        var jalons = new[] { 30, 60, 120, 240, 480 };

        for (var t = 0; t < 2000; t++)
        {
            id.Feed(Note(t), 0, 64, 0.8f);
            if (jalons.Contains(t + 1)) releves.Add(id.Brightness);
        }

        var final = id.Brightness;
        var ecarts = releves.Select(v => MathF.Abs(v - final)).ToList();

        // L'ecart au portrait final se resserre nettement entre le premier palier et le
        // dernier. On ne l'exige pas strictement decroissant a chaque etape : la
        // convergence d'une moyenne sur un signal bruite n'est pas monotone image par
        // image, et l'exiger testerait le tirage plutot que la methode.
        Assert.True(ecarts[^1] < ecarts[0] * 0.5f,
                    $"apres {jalons[0]} observations : {ecarts[0]:F5} ; " +
                    $"apres {jalons[^1]} : {ecarts[^1]:F5}");
    }

    /// <summary>Ce qu'on range se relit tel quel, y compris apres un aller-retour disque.</summary>
    [Fact]
    public void La_connaissance_survit_au_disque()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "emotion-test-" + Guid.NewGuid());
        try
        {
            var magasin = new KnowledgeStore(dossier);
            var tracker = new VoiceTracker(48_000, 1024);

            var spectre = new float[512];
            for (var i = 0; i < spectre.Length; i++) spectre[i] = 0.4f + (i % 7) * 0.08f;
            for (var t = 0; t < 400; t++) tracker.Feed(spectre);

            tracker.Nommer(2, 42);
            var avant = tracker.Portraits();
            magasin.Save(new TrackKnowledge("Macroblank — two sided", avant, 87.6f, 900, 24f));

            var relu = magasin.Load("Macroblank — two sided");
            Assert.True(relu.Any);
            Assert.Equal(87.6f, relu.Bpm);
            Assert.Equal(24f, relu.SecondsHeard);

            var suite = new VoiceTracker(48_000, 1024);
            suite.Reprendre(relu);

            for (var r = 0; r < Voices.Registers; r++)
                Assert.Equal(avant[r].Observations, suite.PortraitDe(r).Observations);

            Assert.Equal((byte)42, relu.Sources[2].Label);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }

    /// <summary>Un morceau jamais entendu ne rend rien, et surtout pas une erreur.</summary>
    [Fact]
    public void Un_morceau_inconnu_part_de_rien()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "emotion-test-" + Guid.NewGuid());
        try
        {
            var k = new KnowledgeStore(dossier).Load("jamais entendu");
            Assert.False(k.Any);
            Assert.Equal(0f, k.Bpm);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }
}
