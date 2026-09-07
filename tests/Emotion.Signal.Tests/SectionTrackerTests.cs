using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class SectionTrackerTests
{
    private static string Dump(SectionTracker t) =>
        string.Join(" ", SectionTracker.Candidates.Zip(t.Scores, (c, v) => $"{c}:{v:F3}"));

    /// <summary>
    /// Joue des mesures dont la signature se repete toutes les <paramref name="phrase"/>
    /// mesures, avec un peu de bruit pour que rien ne soit exactement identique.
    /// </summary>
    private static SectionTracker Play(int phrase, int bars, int seed = 4)
    {
        var t = new SectionTracker();
        var rnd = new Random(seed);

        // Une signature propre a chaque rang dans la phrase : c'est ce qui fait qu'une
        // phrase se reconnait, la mesure 3 ressemblant a la mesure 3 de la phrase
        // precedente et non a sa voisine.
        var motifs = new float[phrase][];
        for (var i = 0; i < phrase; i++)
        {
            motifs[i] = new float[12];
            for (var b = 0; b < 12; b++) motifs[i][b] = (float)rnd.NextDouble();
        }

        for (var bar = 0; bar < bars; bar++)
        {
            var m = motifs[bar % phrase];
            for (var f = 0; f < 30; f++)
            {
                var bands = new float[12];
                for (var b = 0; b < 12; b++)
                    bands[b] = Math.Clamp(m[b] + (float)(rnd.NextDouble() - 0.5) * 0.15f, 0f, 1f);

                t.Feed(bands, m[6], m[9]);
            }

            t.CloseBar();
        }

        return t;
    }

    [Fact]
    public void Une_phrase_de_huit_mesures_est_reconnue()
    {
        var t = Play(phrase: 8, bars: 48);

        Assert.Equal(8, t.PhraseBars);
        Assert.True(t.Confidence > 0.3f, $"conf {t.Confidence:F2} best {t.BestScore:F3} scores {Dump(t)}");
    }

    [Fact]
    public void Une_phrase_de_quatre_mesures_est_reconnue()
    {
        var t = Play(phrase: 4, bars: 40);

        Assert.Equal(4, t.PhraseBars);
    }

    [Fact]
    public void Sans_repetition_le_suivi_ne_pretend_rien()
    {
        // LE TEST QUI ATTRAPE LE FAUX POSITIF QUE J'AI EU.
        //
        // Le suivi rendait « huit mesures, 100 % du temps » sur un vrai set, et
        // l'intervalle entre phrases tombait pile sur la duree theorique de huit mesures.
        // Tout concordait — sauf que c'etait la valeur par defaut, jamais modifiee : une
        // hypothese sans assez de donnees valait zero, les scores reels etant negatifs,
        // et l'estimateur sortait sans rien decider.
        //
        // Un suivi qui n'a rien decide doit le dire, et une valeur par defaut qui a l'air
        // juste est plus dangereuse qu'une erreur franche.
        var t = new SectionTracker();
        var rnd = new Random(9);

        for (var bar = 0; bar < 40; bar++)
        {
            for (var f = 0; f < 30; f++)
            {
                var bands = new float[12];
                for (var b = 0; b < 12; b++) bands[b] = (float)rnd.NextDouble();
                t.Feed(bands, (float)rnd.NextDouble(), (float)rnd.NextDouble());
            }

            t.CloseBar();
        }

        Assert.True(t.Confidence < 0.5f, $"invente : conf {t.Confidence:F2} best {t.BestScore:F3} scores {Dump(t)}");
    }

    [Fact]
    public void Le_rang_dans_la_phrase_avance_sans_sauter()
    {
        // Il se deduit d'un compteur absolu. Entretenu a la main, il etait recalcule par
        // la recherche de frontiere puis incremente dans la foulee, et sautait a chaque
        // hesitation sur la longueur.
        var t = Play(phrase: 8, bars: 40);
        var first = t.BarInPhrase;

        var seen = new List<int> { first };
        for (var bar = 0; bar < 8; bar++)
        {
            for (var f = 0; f < 30; f++) t.Feed(stackalloc float[12], 0.5f, 0.5f);
            t.CloseBar();
            seen.Add(t.BarInPhrase);
        }

        // Huit mesures de plus ramenent au meme rang, sans discontinuite.
        Assert.Equal(first, seen[^1]);
        Assert.All(seen, r => Assert.InRange(r, 0, t.PhraseBars - 1));
    }

    [Fact]
    public void Un_motif_de_batterie_ne_passe_pas_pour_une_phrase()
    {
        // Deux mesures ne figurent pas parmi les hypotheses : a cette echelle on ne decrit
        // plus une phrase mais la boucle que le batteur repete a l'interieur. Elle se
        // correle mieux que la vraie phrase et n'apprend rien — mesure faite en la
        // laissant : elle l'emportait deux fois sur trois.
        var t = Play(phrase: 2, bars: 40);

        Assert.True(t.PhraseBars >= 4, $"{t.PhraseBars} mesures retenues");
    }
}
