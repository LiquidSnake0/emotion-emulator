using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// LE PAS D'ANALYSE EGALE LA FENETRE, ET LA FENETRE EST UNE HANN.
///
/// Une Hann vaut zero a ses deux bords. Sans recouvrement, chaque instant du signal n'est
/// donc vu qu'une seule fois, et avec un poids qui depend entierement de sa position dans
/// la fenetre : plein au centre, nul a la couture. Une frappe qui tombe sur une couture est
/// eteinte des deux cotes a la fois.
///
/// C'est la raison pour laquelle l'usage veut un pas d'une demi-fenetre ou d'un quart. Ces
/// tests mesurent ce que ce choix coute ici, avec de vraies frappes placees exprès a
/// chaque endroit de la fenetre.
/// </summary>
public class CoutureTests
{
    private const int Rate = 48_000;
    private const int W = SpectrumAnalyzer.Window;

    /// <summary>
    /// Une frappe grave et seche, comme un kick : une sinusoide a 60 Hz sous une enveloppe
    /// qui monte en une milliseconde et retombe en quarante.
    /// </summary>
    private static void PoserFrappe(float[] signal, int a)
    {
        const int duree = Rate / 25;                       // 40 ms
        for (var k = 0; k < duree && a + k < signal.Length; k++)
        {
            var t = k / (float)Rate;
            var montee = MathF.Min(1f, k / (Rate * 0.001f));
            var enveloppe = montee * MathF.Exp(-t * 45f);
            signal[a + k] += MathF.Sin(2f * MathF.PI * 60f * t) * enveloppe * 0.9f;
        }
    }

    /// <summary>
    /// Fait passer un signal dans l'analyseur et rend les instants ou un kick a ete vu.
    /// </summary>
    private static List<long> Ecouter(float[] signal)
    {
        var a = new SpectrumAnalyzer(Rate);
        var vus = new List<long>();
        for (var i = 0; i + W <= signal.Length; i += W)
        {
            var tMs = i * 1000L / Rate;
            if (a.Analyze(signal.AsSpan(i, W), tMs).Hits.Kick) vus.Add(tMs);
        }
        return vus;
    }

    /// <summary>Les memes, mais dates par l'analyseur et non par la fenetre.</summary>
    private static List<long> EcouterInstants(float[] signal)
    {
        var a = new SpectrumAnalyzer(Rate);
        var vus = new List<long>();
        for (var i = 0; i + W <= signal.Length; i += W)
        {
            var tMs = i * 1000L / Rate;
            if (a.Analyze(signal.AsSpan(i, W), tMs).Hits.Kick) vus.Add(a.FrappeMs);
        }
        return vus;
    }

    /// <summary>
    /// LE TEST QU'AUCUNE MESURE INTERNE NE POUVAIT REMPLACER.
    ///
    /// L'instant publie doit tomber sur la frappe, et non sur la fenetre qui l'a jugee. La
    /// distinction n'est pas academique : la grille se cale sur cet instant, et l'horloge
    /// a verrouillage de phase predit ses temps a partir de cette grille — un biais sur
    /// l'origine devient un retard sur chaque temps annonce.
    ///
    /// AUCUN INDICATEUR INTERNE NE POUVAIT LE VOIR. Ils comparent tous les frappes a la
    /// grille, laquelle est calee sur ces memes frappes : un decalage commun aux deux est
    /// invisible par construction. Il a fallu confronter nos instants a ceux d'une autre
    /// implementation — l'ecart median valait alors +17 ms sur un metronome dont on savait
    /// pourtant qu'il etait parfait.
    ///
    /// La cause tenait en une ligne, sous un commentaire qui decrivait la bonne intention :
    /// on datait la frappe de `tMs + offset_de_la_fenetre_courante` alors qu'elle avait ete
    /// jugee sur la precedente, et que l'offset a employer etait celui de cette
    /// precedente-la. Deux erreurs de meme sens, une dizaine de millisecondes ajoutees la
    /// ou il fallait en retirer une vingtaine.
    /// </summary>
    [Fact]
    public void L_instant_publie_tombe_sur_la_frappe_et_non_sur_la_fenetre()
    {
        const int frappes = 40;
        const int ecart = W * 24;
        var signal = new float[ecart * (frappes + 4)];
        var poses = new List<long>();
        for (var n = 2; n < frappes + 2; n++)
        {
            var a = n * ecart + W / 3;             // volontairement pas sur une couture
            PoserFrappe(signal, a);
            poses.Add(a * 1000L / Rate);
        }

        var vus = EcouterInstants(signal);
        Assert.True(vus.Count >= frappes - 4, $"{vus.Count} frappes vues sur {frappes}");

        var ecarts = new List<float>();
        foreach (var v in vus)
        {
            var meilleur = float.MaxValue;
            foreach (var p in poses)
                if (MathF.Abs(v - p) < MathF.Abs(meilleur)) meilleur = v - p;
            ecarts.Add(meilleur);
        }
        ecarts.Sort();
        var median = ecarts[ecarts.Count / 2];

        // Une demi-fenetre : on ne peut pas faire mieux que la resolution d'analyse, mais
        // on ne doit pas faire pire d'une fenetre entiere.
        Assert.True(MathF.Abs(median) < 11f,
            $"les frappes sont publiees avec {median:F0} ms d'ecart systematique " +
            $"(une fenetre en vaut {W * 1000f / Rate:F0})");
    }

    /// <summary>
    /// LA MESURE QUI TRANCHE. La meme frappe, repetee au meme intervalle, mais decalee
    /// d'un seizieme de fenetre a chaque essai. Si le fenetrage n'y est pour rien, les
    /// seize essais doivent se ressembler.
    /// </summary>
    [Fact]
    public void Une_frappe_est_vue_ou_qu_elle_tombe_dans_la_fenetre()
    {
        const int frappes = 40;
        const int ecart = W * 24;                          // ~512 ms, un temps a 117 BPM
        var vues = new int[16];

        for (var d = 0; d < 16; d++)
        {
            var decalage = d * W / 16;
            var signal = new float[ecart * (frappes + 4)];
            for (var n = 2; n < frappes + 2; n++) PoserFrappe(signal, n * ecart + decalage);
            vues[d] = Ecouter(signal).Count;
        }

        var min = vues.Min();
        var max = vues.Max();
        Assert.True(min > 0, $"aucune frappe vue a un decalage : {string.Join(" ", vues)}");
        Assert.True(min >= max * 0.75f,
            $"la detection depend de l'endroit ou la frappe tombe dans la fenetre : " +
            $"{string.Join(" ", vues)} (de {min} a {max} sur {frappes})");
    }

    /// <summary>
    /// Et le retard, lui aussi, ne doit pas dependre de l'endroit. Un decalage systematique
    /// se corrige ; un decalage qui varie avec la position ne se corrige pas, il se
    /// constate en gigue.
    /// </summary>
    [Fact]
    public void Le_retard_ne_depend_pas_de_l_endroit_de_la_fenetre()
    {
        const int frappes = 40;
        const int ecart = W * 24;
        var retards = new List<float>();

        for (var d = 0; d < 16; d++)
        {
            var decalage = d * W / 16;
            var signal = new float[ecart * (frappes + 4)];
            var poses = new List<long>();
            for (var n = 2; n < frappes + 2; n++)
            {
                var a = n * ecart + decalage;
                PoserFrappe(signal, a);
                poses.Add(a * 1000L / Rate);
            }

            var vus = Ecouter(signal);
            if (vus.Count < frappes / 2) continue;

            // Pour chaque frappe vue, l'ecart a la frappe posee la plus proche.
            var somme = 0f;
            foreach (var v in vus)
            {
                var meilleur = float.MaxValue;
                foreach (var p in poses) meilleur = MathF.Min(meilleur, MathF.Abs(v - p));
                somme += meilleur;
            }
            retards.Add(somme / vus.Count);
        }

        Assert.True(retards.Count >= 12, $"trop de decalages sans detection : {retards.Count}/16");
        var etendue = retards.Max() - retards.Min();
        Assert.True(etendue < 21f,
            $"le retard varie de {etendue:F0} ms selon l'endroit dans la fenetre " +
            $"({retards.Min():F0} a {retards.Max():F0})");
    }
}
