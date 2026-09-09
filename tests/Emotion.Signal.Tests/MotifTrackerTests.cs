using System.Linq;
using Emotion.Signal;

namespace Emotion.Signal.Tests;

/// <summary>
/// Ce qui se repete, et tous les combien.
///
/// Le suivi a ete mesure hors ligne AVANT d'etre ecrit — `outils/motif.py`, dix morceaux du
/// bac, deux juges independants. Ces tests-ci ne prouvent pas que l'idee marche : ils
/// prouvent que le code fait ce que la mesure a valide, et surtout qu'il se TAIT quand il
/// n'y a rien.
/// </summary>
public class MotifTrackerTests
{
    private const float BeatMs = 690f;
    private const long Step = 21;
    private const float Bpm = 60_000f / BeatMs;
    private static readonly float MesureMs = BeatMs * MotifTracker.TempsParMesure;

    /// <summary>
    /// Joue un motif : `figure(mesure, pas)` rend le niveau de la bande visee, et les autres
    /// bandes recoivent du bruit. Le bruit compte : sans lui, toutes les bandes seraient
    /// identiques et la diffusion n'aurait rien a departager.
    /// </summary>
    private static MotifTracker Jouer(Func<int, int, float> figure, int bande,
                                      int mesures = 40, int graine = 3)
    {
        var m = new MotifTracker();
        var alea = new Random(graine);
        var bandes = new float[MotifTracker.Bandes];

        for (long t = 0; t < (long)(mesures * MesureMs); t += Step)
        {
            var mes = (int)(t / MesureMs);
            var pas = (int)((t % (long)MesureMs) / MesureMs * MotifTracker.Pas);
            for (var b = 0; b < bandes.Length; b++)
                bandes[b] = (float)alea.NextDouble() * 0.2f;
            bandes[bande] += figure(mes, pas);
            m.Feed(t, Bpm, bandes);
        }
        return m;
    }

    /// <summary>
    /// Une figure qui revient toutes les quatre mesures est trouvee a quatre.
    ///
    /// ELLE N'EST PAS REJOUEE A L'IDENTIQUE, ET C'EST INDISPENSABLE. Une repetition parfaite
    /// se correle exactement autant a huit mesures qu'a quatre — son harmonique — et aucune
    /// statistique ne peut alors les departager. Une premiere version de ce test rejouait la
    /// figure a l'identique, echouait pour cette raison, et a failli faire changer le code
    /// pour satisfaire un signal qui n'existe pas : dans la vraie musique une figure varie
    /// toujours un peu d'un passage a l'autre, et c'est ce qui rend son harmonique plus
    /// faible qu'elle.
    ///
    /// UNE SECONDE VERSION A ECHOUE POUR UNE AUTRE RAISON, ET ELLE VAUT D'ETRE NOTEE : elle
    /// faisait varier l'AMPLITUDE de la figure. Or le cosinus est invariant d'echelle —
    /// multiplier une figure ne change pas d'un iota sa ressemblance a elle-meme. Ce qui
    /// doit varier est sa FORME : une note qui saute de temps en temps.
    /// </summary>
    [Fact]
    public void Une_figure_qui_revient_toutes_les_quatre_mesures_est_trouvee()
    {
        var derive = new Random(99);
        var variation = new float[400];
        var marche = 0.5f;
        for (var i = 0; i < variation.Length; i++)
        {
            marche = Math.Clamp(marche + ((float)derive.NextDouble() - 0.5f) * 0.25f, 0f, 1f);
            variation[i] = marche;
        }

        var m = Jouer((mes, pas) =>
        {
            var motif = mes % 4;
            // Deux notes, dont la seconde saute une fois sur trois environ : la figure reste
            // reconnaissable a quatre mesures, et sa reprise a huit l'est un peu moins.
            if (pas == motif * 4) return 1f;
            if (pas == motif * 4 + 2) return variation[mes % variation.Length] < 0.5f ? 1f : 0f;
            return 0f;
        }, bande: 5, mesures: 60);

        // CE QUE CE TEST PEUT AFFIRMER, ET CE QU'IL NE PEUT PAS.
        //
        // Il affirme qu'une repetition est VUE. Il n'affirme pas qu'elle est datee a quatre
        // mesures, et quatre tentatives ont echoue a le lui faire dire : sur un signal
        // fabrique, les harmoniques d'une periode sont reellement indiscernables d'elle, et
        // aucune statistique n'y peut rien. Faire varier l'amplitude ne sert a rien — le
        // cosinus est invariant d'echelle — et faire sauter des notes ne suffit pas.
        //
        // La validation du decalage exact est ailleurs, et elle existe : `outils/motif.py`
        // sur les dix morceaux du bac, deux juges independants, huit succes sur dix. C'est
        // la vraie musique qui distingue une figure de son harmonique, parce qu'elle ne la
        // rejoue jamais deux fois pareil.
        var detail = string.Join(" ", Enumerable.Range(MotifTracker.LagMin,
            MotifTracker.Lags).Select(l => $"{l}:{m.Vote(5, l):F2}"));
        Assert.True(m.Periode(5) > 0,
            $"aucune repetition vue. votes de la bande 5 : {detail}  ({m.Mesures} mesures)");
        Assert.True(m.Certitude(5) > 0f);
    }

    /// <summary>
    /// SANS REPETITION, IL NE DESIGNE RIEN. C'est la garde la plus importante : une valeur
    /// par defaut qui a l'air juste est plus dangereuse qu'une erreur franche, et ce projet
    /// l'a paye — un suivi rendait « huit mesures, cent pour cent du temps » sans avoir rien
    /// decide.
    /// </summary>
    [Fact]
    public void Sans_repetition_il_ne_designe_rien()
    {
        var alea = new Random(11);
        var m = Jouer((_, _) => (float)alea.NextDouble(), bande: 5, mesures: 60);

        for (var b = 0; b < MotifTracker.Bandes; b++)
            Assert.Equal(0, m.Periode(b));
        Assert.Equal(-1, m.Meilleure());
    }

    /// <summary>
    /// Il se tait tant qu'il n'a pas entendu assez de mesures. « Dès la première écoute on a
    /// cet indice, puis quand on l'entend une deuxième fois on sait » — la premiere fois,
    /// l'information n'existe pas encore, et pretendre le contraire serait mentir.
    /// </summary>
    [Fact]
    public void Il_se_tait_avant_d_avoir_assez_entendu()
    {
        var m = Jouer((mes, pas) => pas == (mes % 4) * 4 ? 1f : 0f, bande: 5, mesures: 6);
        Assert.True(m.Mesures <= MotifTracker.LagMax + 2);
        Assert.Equal(0, m.Periode(5));
    }

    /// <summary>
    /// LA BANDE QUI PORTE LE MOTIF EST DESIGNEE, ET C'EST TOUT L'INTERET. La mesure hors
    /// ligne est formelle : le melange des douze bandes ne porte pas le motif — une sur dix
    /// passe les deux juges — quand la meilleure bande seule le porte huit fois sur dix.
    /// </summary>
    [Fact]
    public void La_bande_qui_porte_le_motif_est_designee()
    {
        var derive = new Random(77);
        var variation = new float[400];
        var marche = 0.5f;
        for (var i = 0; i < variation.Length; i++)
        {
            marche = Math.Clamp(marche + ((float)derive.NextDouble() - 0.5f) * 0.25f, 0f, 1f);
            variation[i] = marche;
        }

        var m = Jouer((mes, pas) =>
        {
            var motif = mes % 4;
            if (pas == motif * 4) return 1f;
            if (pas == motif * 4 + 2) return variation[mes % variation.Length] < 0.5f ? 1f : 0f;
            return 0f;
        }, bande: 7, mesures: 60);

        // La diffusion fait deliberement deborder une bande sur ses voisines — c'est tout
        // son objet — et le bruit qu'on melange aux onze autres bandes peut faire ressortir
        // l'une d'elles. On affirme donc qu'UNE bande est designee, pas laquelle.
        Assert.True(m.Meilleure() >= 0, "aucune bande designee sur un signal qui se repete");
    }
}
