using Emotion.Pulse;

namespace Emotion.Signal.Tests;

/// <summary>
/// LA MESURE QUI MANQUAIT, ET QU'IL FAUT DONC VERIFIER PLUS QUE LES AUTRES.
///
/// Le projet disposait de deux familles d'indicateurs et aucune ne repondait a la question
/// qui decide du detecteur. Les indicateurs internes se notent contre une grille calee sur
/// ce qu'ils notent. La confrontation a une autre implementation dit « est-ce un vrai
/// evenement », jamais « est-ce le BON evenement » — un detecteur qui tirerait sur toutes
/// les attaques y excellerait en rendant la grille inutilisable.
///
/// Celle-ci demande : ces instants forment-ils un pouls ? Elle ne consulte aucune grille.
/// Comme elle sert desormais a trancher, elle doit etre juste sur les cas dont on connait
/// la reponse — un pouls parfait, du hasard, un contretemps, une derive.
/// </summary>
public class PulsationTests
{
    private const double Bpm = 87.85;
    private static readonly double Temps = 60.0 / Bpm;

    private static List<double> Metronome(int n, double depart = 0, double periode = 0) =>
        Enumerable.Range(0, n).Select(i => depart + i * (periode > 0 ? periode : Temps)).ToList();

    [Fact]
    public void Un_pouls_parfait_donne_une_force_pleine_et_le_bon_tempo()
    {
        var (p, stabilite, fenetres) = Pulsation.ChercherLocal(Metronome(130));

        Assert.True(p.Force > 0.95, $"force {p.Force:F3}");
        Assert.Equal(1.0, stabilite);
        Assert.True(fenetres >= 8, $"{fenetres} fenetres");
        Assert.InRange(p.Bpm, Bpm - 1.5, Bpm + 1.5);
    }

    /// <summary>
    /// Des instants au hasard doivent tomber sur le niveau de hasard, et non au-dessus.
    /// Sans quoi tous les chiffres du detecteur seraient flattes de la meme facon.
    /// </summary>
    [Fact]
    public void Le_hasard_ne_produit_pas_de_pouls()
    {
        var alea = new Random(11);
        var t = Enumerable.Range(0, 130).Select(_ => alea.NextDouble() * 90.0).OrderBy(x => x).ToList();
        var (p, stabilite, _) = Pulsation.ChercherLocal(t);

        // La recherche prend le meilleur de mille periodes : elle depasse forcement un peu
        // le hasard theorique. Ce qui doit s'effondrer, c'est la stabilite — le hasard ne
        // retrouve pas deux fois la meme periode.
        Assert.True(stabilite < 0.5, $"stabilite {stabilite:P0} sur du bruit");
        Assert.True(p.Force < 0.75, $"force {p.Force:F3} sur du bruit");
    }

    /// <summary>
    /// POURQUOI LA MESURE EST LOCALE. Une derive de tempo d'un pour cent — banale sur un
    /// vinyle joue au fader — suffit a effondrer une mesure globale : sur cent trente
    /// temps, elle accumule plus d'un temps entier d'ecart et les vecteurs finissent par
    /// pointer partout. Mesure a l'appui : sur un morceau reel, la force valait 0,062 a
    /// 690 ms et 0,268 a 696 ms.
    ///
    /// Par fenetres de quinze secondes, la meme derive n'a pas le temps de s'accumuler.
    /// </summary>
    [Fact]
    public void Une_derive_de_tempo_n_effondre_plus_la_mesure()
    {
        var t = new List<double>();
        var instant = 0.0;
        var periode = Temps;
        for (var i = 0; i < 130; i++)
        {
            t.Add(instant);
            instant += periode;
            periode *= 1.0001;          // environ +1,3 % sur toute la duree
        }

        var globale = Pulsation.Chercher(t).Force;
        var (locale, stabilite, _) = Pulsation.ChercherLocal(t);

        Assert.True(locale.Force > 0.90, $"la mesure locale devrait tenir : {locale.Force:F3}");
        Assert.True(stabilite > 0.8, $"stabilite {stabilite:P0}");
        Assert.True(locale.Force > globale,
            $"locale {locale.Force:F3} vs globale {globale:F3} — le decoupage doit servir a quelque chose");
    }

    /// <summary>
    /// LE CAS QUE RIEN NE SAVAIT DIRE AVANT. Un detecteur peut etre parfaitement regulier
    /// et parfaitement a cote : s'il tire sur les contretemps, ses intervalles sont
    /// impeccables et son visuel est faux. L'accord vaut alors -1, et c'est la seule
    /// grandeur du projet qui sache distinguer ce cas d'un detecteur juste.
    /// </summary>
    [Fact]
    public void Un_detecteur_a_contretemps_est_regulier_et_faux()
    {
        var reference = Metronome(60);
        var pouls = Pulsation.Chercher(reference);

        var surLeTemps = Metronome(60);
        var surLeContretemps = Metronome(60, depart: Temps / 2);

        Assert.True(Pulsation.Accord(surLeTemps, pouls) > 0.98);
        Assert.True(Pulsation.Accord(surLeContretemps, pouls) < -0.98);

        // Et pourtant les deux sont aussi reguliers l'un que l'autre : la regularite ne dit
        // rien de la justesse, et c'est precisement pourquoi il fallait une autre mesure.
        var fTemps = Pulsation.Chercher(surLeTemps).Force;
        var fContre = Pulsation.Chercher(surLeContretemps).Force;
        Assert.True(fTemps > 0.98 && fContre > 0.98, $"{fTemps:F3} et {fContre:F3}");
        Assert.True(Math.Abs(fTemps - fContre) < 0.02,
            $"la force ne doit pas distinguer les deux : {fTemps:F3} contre {fContre:F3}");
    }

    /// <summary>
    /// Des instants poses sur une grille de periode P tombent aussi, exactement, sur une
    /// grille de P/2 et de P/3. La force ne peut donc que croitre quand la periode
    /// raccourcit, et un simple maximum choisirait toujours la plus courte periode
    /// exploree. On retient la plus longue des meilleures.
    /// </summary>
    [Fact]
    public void La_periode_rendue_n_est_pas_une_sous_division()
    {
        var p = Pulsation.Chercher(Metronome(130));
        Assert.InRange(p.Bpm, Bpm - 1.0, Bpm + 1.0);
        Assert.True(p.Periode > 0.6, $"periode {p.Periode * 1000:F0} ms — une sous-division a ete prise");
    }

    /// <summary>
    /// Le rapport entre deux periodes doit se lire en fraction simple : « le double » et
    /// « la moitie » sont des erreurs d'octave, pas des tempos differents, et les
    /// confondre ferait accuser un detecteur qui a raison.
    /// </summary>
    [Fact]
    public void Les_erreurs_d_octave_se_nomment()
    {
        Assert.Equal("le meme", Pulsation.Rapport(0.683, 0.683));
        Assert.Equal("le double", Pulsation.Rapport(1.366, 0.683));
        Assert.Equal("la moitie", Pulsation.Rapport(0.342, 0.683));
        Assert.Equal("deux tiers", Pulsation.Rapport(0.455, 0.683));
    }

    /// <summary>
    /// Trop peu d'instants : on rend zero plutot qu'un chiffre inventé. Huit frappes
    /// suffiraient a faire dire n'importe quoi a une recherche sur mille periodes.
    /// </summary>
    [Fact]
    public void Trop_peu_d_instants_ne_rend_rien()
    {
        var (p, _, fenetres) = Pulsation.ChercherLocal([1.0, 2.0, 3.0]);
        Assert.Equal(0, p.Periode);
        Assert.Equal(0, fenetres);
    }
}
