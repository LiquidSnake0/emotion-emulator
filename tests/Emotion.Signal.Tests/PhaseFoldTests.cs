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
