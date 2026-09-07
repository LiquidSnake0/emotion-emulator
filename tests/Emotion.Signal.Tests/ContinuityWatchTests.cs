using Emotion.Signal;

namespace Emotion.Signal.Tests;

public class ContinuityWatchTests
{
    [Fact]
    public void Un_disque_qui_joue_droit_ne_declenche_rien()
    {
        var w = new ContinuityWatch();
        var breaks = 0;

        for (var i = 0; i < 400; i++)
        {
            // Une frappe tous les 32 fenetres, calee a quelques centiemes pres.
            var kick = i % 32 == 0;
            w.Feed(0.5f, kick, kick ? (i % 3 - 1) * 0.03f : 0f);
            if (w.Broken) breaks++;
        }

        Assert.Equal(0, breaks);
    }

    [Fact]
    public void Un_beatmatch_ne_passe_pas_pour_un_accident()
    {
        // LE TEST QUI DICTE TOUTE LA CONCEPTION.
        //
        // Pousser ou retenir le disque pour le recaler produit exactement l'ecart de phase
        // qu'un seuil naif prendrait pour un saut. Le geste est pourtant le plus banal du
        // metier, et decrocher a chaque fois reviendrait a decrocher tout le temps.
        //
        // Ce qui distingue les deux n'est pas l'amplitude mais la forme : un bend fait
        // croitre l'ecart puis le ramene, dans un sens, en plusieurs temps.
        var w = new ContinuityWatch();
        var breaks = 0;

        var errors = new[] { 0.05f, 0.14f, 0.28f, 0.38f, 0.30f, 0.18f, 0.06f, 0.02f };
        var k = 0;

        for (var i = 0; i < 400; i++)
        {
            var kick = i % 32 == 0;
            var e = kick ? errors[k++ % errors.Length] : 0f;
            w.Feed(0.5f, kick, e);
            if (w.Broken) breaks++;
        }

        Assert.Equal(0, breaks);
    }

    [Fact]
    public void Un_saut_de_sillon_finit_par_etre_constate_mais_tard()
    {
        // CE TEST DOCUMENTE UNE LIMITE PLUTOT QU'UNE REUSSITE.
        //
        // Le detecteur constate le saut, mais il lui faut une soixantaine de frappes, soit
        // une quinzaine de mesures — presque une minute. C'est trop tard pour servir a
        // quoi que ce soit en direct.
        //
        // La cause n'est pas un reglage mal choisi, c'est le critere lui-meme. Apres un
        // saut, l'ecart de phase n'est plus une erreur mais un tirage : la moitie des
        // frappes tombent pres de la grille par coincidence, et une part d'entre elles
        // dans la continuite de la precedente — ce qui ressemble exactement a un disque en
        // bonne sante. Le durcir pour eviter les faux positifs, qui eux coutaient dix
        // ruptures par cent secondes sur un set sans le moindre saut, l'a rendu lent.
        //
        // Un saut change surtout <b>ce qu'on entend</b> : le spectre et l'harmonie sautent
        // ailleurs dans le disque. Le detecter par le contenu plutot que par la phase est
        // la piste, et elle n'est pas ouverte.
        var w = new ContinuityWatch();
        var rnd = new Random(3);
        var broken = false;

        for (var i = 0; i < 30; i++) w.Feed(0.5f, i % 3 == 0, 0.02f);

        // Apres un saut, l'ecart n'est plus une erreur mais un tirage : la grille n'a
        // plus aucun rapport avec ce qui joue, donc l'ecart se repartit sur tout le
        // domaine. Pres de la moitie des frappes tombent alors pres de la grille par pure
        // coincidence — c'est precisement pour cela qu'on integre au lieu de compter.
        var strayFrames = 0;
        for (var i = 0; i < 300 && !broken; i++)
        {
            var kick = i % 3 == 0;
            w.Feed(0.5f, kick, kick ? (float)(rnd.NextDouble() - 0.5) : 0f);
            if (kick) strayFrames++;
            if (w.Broken) broken = true;
        }

        Assert.True(broken, $"un saut doit etre constate ({strayFrames} frappes)");
        // On decroche tard, et c'est voulu. Mesure sur un vrai set : a seuil bas, la
        // detection voyait jusqu'a dix ruptures en cent secondes sans qu'aucun disque ne
        // saute, et chacune effacait un temps fort acquis en une minute d'ecoute. Un faux
        // positif coute bien plus cher qu'un decrochage tardif.
        Assert.True(strayFrames <= 90, $"{strayFrames} frappes : encore plus lent que constate");
        Assert.Equal("frappes hors grille", w.Reason);
        Assert.False(w.WasSilence);
    }

    [Fact]
    public void Une_frappe_egaree_isolee_ne_prouve_rien()
    {
        // Le desordre doit etre continu. Une detection ratee de temps en temps est le lot
        // ordinaire, et une juste efface le compte au lieu de le decrementer.
        var w = new ContinuityWatch();
        var breaks = 0;

        for (var i = 0; i < 400; i++)
        {
            var kick = i % 32 == 0;
            var e = kick && i % 128 == 0 ? 0.45f : 0.03f;   // une sur quatre, isolee
            w.Feed(0.5f, kick, kick ? e : 0f);
            if (w.Broken) breaks++;
        }

        Assert.Equal(0, breaks);
    }

    [Fact]
    public void Un_arret_est_constate_et_se_distingue_d_un_saut()
    {
        // La nuance decide de ce qu'on jette : un saut laisse le meme disque a la meme
        // vitesse, donc le tempo reste valable. Un silence annonce autre chose.
        var w = new ContinuityWatch();
        for (var i = 0; i < 20; i++) w.Feed(0.5f, i % 3 == 0, 0.02f);

        var broken = false;
        // Trois secondes de silence, pas une : un break qui vide le morceau un instant
        // n'est pas un disque qui s'arrete, et les confondre fait jeter le tempo juste
        // avant le retour.
        for (var i = 0; i < 200 && !broken; i++)
        {
            w.Feed(0.001f, false, 0f);
            if (w.Broken) broken = true;
        }

        Assert.True(broken, "un arret doit etre constate");
        Assert.True(w.WasSilence);
    }

    [Fact]
    public void La_rupture_ne_vaut_que_pour_une_fenetre()
    {
        // Republier le verdict remettrait a zero indefiniment ce qu'on essaie justement
        // de reconstruire.
        var w = new ContinuityWatch();
        var rnd = new Random(11);
        var breaks = 0;

        for (var i = 0; i < 200; i++)
        {
            w.Feed(0.5f, i % 3 == 0, (float)(rnd.NextDouble() * 0.4 + 0.3));
            if (w.Broken) breaks++;
        }

        // Une rupture toutes les trois frappes egarees, pas une par fenetre.
        Assert.InRange(breaks, 1, 30);
    }
}
