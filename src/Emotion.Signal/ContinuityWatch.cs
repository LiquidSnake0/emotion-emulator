namespace Emotion.Signal;

/// <summary>
/// Surveille que le disque joue toujours ce qu'on croit qu'il joue.
///
/// LE PROBLEME. Un vinyle saute, on le repousse, on le scratche, on l'arrete. Rien de tout
/// cela n'existe pour un fichier, et tout cela arrive en vrai. Apres un saut de sillon, la
/// grille metrique, les votes du temps fort et la structure decrivent un endroit du disque
/// ou l'on n'est plus — et ils mettront plus d'une minute a l'admettre, puisque leur
/// oubli est lent par construction.
///
/// Le jour ou une partition prealable sera jouee depuis le cue, la meme rupture rendra
/// cette partition entierement fausse : le systeme n'aurait plus de retard, il aurait tort.
/// <b>Un visuel en retard reste lisible ; un visuel qui invente ne l'est pas.</b>
///
/// LE PIEGE, ET C'EST LUI QUI DICTE LA CONCEPTION. Le reflexe est de seuiller l'ecart de
/// phase : au-dela d'un quart de temps, on decroche. Ce serait faux, parce que le geste le
/// plus banal d'un DJ — pousser ou retenir le disque pour recaler — produit exactement cet
/// ecart-la. Un tel seuil decrocherait a chaque beatmatch, c'est-a-dire tout le temps.
///
/// Ce qui distingue un accident d'un geste n'est pas l'amplitude, c'est la <b>forme</b> :
///
///   un pitch bend    l'ecart croit puis revient, <b>progressivement</b> : d'une frappe a
///                    la suivante il a peu bouge, meme quand il est grand
///   un saut          l'ecart devient quelconque et le reste : d'une frappe a la suivante
///                    il n'a aucun rapport avec le precedent
///
/// Une frappe n'est donc comptee egaree que si elle est <b>a la fois</b> loin de la grille
/// et loin de la frappe precedente. Un ecart de 0,38 qui suit un ecart de 0,28 est un
/// geste ; un ecart de 0,38 qui suit un ecart de -0,20 est un accident. C'est cette
/// seconde condition qui fait toute la difference, et sans elle le detecteur decroche a
/// chaque beatmatch — c'est-a-dire tout le temps.
///
/// Une seule frappe egaree ne prouve rien par ailleurs : c'est le lot ordinaire d'une
/// detection.
///
/// CE QUE CE DETECTEUR FAIT BIEN, ET CE QU'IL NE FAIT PAS. Il repere proprement un arret
/// ou un changement de disque, par le silence. Il finit par reperer un saut de sillon,
/// mais lui-meme et tardivement : une quinzaine de mesures, ce qui ne sert a rien en
/// direct.
///
/// Ce n'est pas un reglage a trouver, c'est le critere qui plafonne. Apres un saut,
/// l'ecart de phase n'est plus une erreur mais un tirage : pres de la moitie des frappes
/// tombent sur la grille par coincidence, et une part d'entre elles dans la continuite de
/// la precedente, ce qui ressemble a un disque en parfaite sante. Le durcir etait pourtant
/// necessaire — a seuil bas il voyait dix ruptures en cent secondes sur un set qui n'en
/// contenait aucune, et chacune effacait un temps fort acquis en une minute d'ecoute.
///
/// Un saut change surtout <b>ce qu'on entend</b>. Le detecter par le contenu — le spectre
/// et l'harmonie qui sautent ailleurs dans le disque — plutot que par la phase est la
/// piste, et elle reste ouverte.
/// </summary>
public sealed class ContinuityWatch
{
    /// <summary>
    /// Ecart de phase, en fraction de temps, au-dela duquel une frappe est dite egaree.
    /// Un quart de temps vaut 170 ms a 88 BPM : bien au-dela de ce qu'un beatmatch produit
    /// image par image, bien en deca de ce qu'un saut produit.
    /// </summary>
    private const float StrayPhase = 0.25f;

    /// <summary>
    /// Desordre accumule valant rupture. On integre au lieu de compter des frappes
    /// consecutives, et la raison est mesurable : <b>apres un saut, les ecarts sont
    /// repartis au hasard</b>, donc pres de la moitie des frappes tombent sur la grille
    /// par pure coincidence. Exiger trois frappes egarees de suite ne se declenchait
    /// donc presque jamais — le test l'a montre immediatement.
    ///
    /// A huit, il faut environ quatre mesures de desordre franc pour decrocher.
    ///
    /// <b>Le seuil valait quatre, et c'etait beaucoup trop bas.</b> Mesure sur cent
    /// secondes d'un set ou aucun disque ne saute : cinq, trois et dix ruptures selon le
    /// passage — chacune effacant la grille, et le verrouillage du temps fort tombant de
    /// 74 a 31 %. Le test unitaire ne pouvait pas le montrer : il supposait une detection
    /// de kick parfaite, quand le vrai signal en produit d'egarees en permanence.
    ///
    /// L'asymetrie commande. Un faux positif detruit ce qu'on a mis une minute a
    /// construire ; un faux negatif laisse la grille fausse le temps que l'oubli fasse
    /// son travail. On decroche donc tard, jamais tot.
    /// </summary>
    private const float DisorderForBreak = 10f;

    /// <summary>
    /// Variation minimale d'une frappe a la suivante pour parler de desordre. En dessous,
    /// l'ecart bouge trop regulierement pour etre autre chose qu'un geste.
    /// </summary>
    private const float StrayJump = 0.18f;

    /// <summary>Niveau sous lequel on considere qu'il ne sort plus rien.</summary>
    private const float SilenceLevel = 0.02f;

    /// <summary>
    /// Fenetres de silence valant arret. Trois secondes environ.
    ///
    /// A une seconde, la sonde comptait trois arrets en cent secondes de set : <b>c'etaient
    /// des breaks</b>. Un morceau qui se vide un instant n'est pas un morceau qui
    /// s'arrete, et confondre les deux fait jeter le tempo au moment precis ou le public
    /// attend le retour.
    /// </summary>
    private const int SilenceForStop = 140;

    private float _disorder;
    private float _previousError;
    private bool _hasPrevious;
    private int _quiet;
    private int _agreements;

    /// <summary>
    /// Vrai sur la seule fenetre qui constate la rupture. C'est un evenement : le republier
    /// ferait remettre a zero indefiniment ce qu'on essaie justement de reconstruire.
    /// </summary>
    public bool Broken { get; private set; }

    /// <summary>Pourquoi, pour le journal et l'ecran de reglage.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>
    /// La rupture est un silence et non un saut. La nuance decide de ce qu'on jette : un
    /// saut de sillon laisse le meme disque a la meme vitesse, donc le tempo reste valable
    /// et seule la position est perdue. Un silence annonce autre chose, et le tempo du
    /// disque precedent n'a plus lieu d'etre.
    /// </summary>
    public bool WasSilence { get; private set; }

    /// <summary>
    /// Frappes consecutives tombees sur la grille. Sert a savoir quand on peut de nouveau
    /// se fier a ce qu'on a reconstruit.
    /// </summary>
    public int Agreements => _agreements;

    /// <summary>
    /// A quel point on peut se fier a la structure, 0 a 1. <b>Elle monte par paliers, et
    /// ce sont ceux du metier.</b>
    ///
    /// Le DJ decrit sa propre facon de valider un calage : il laisse tourner deux temps,
    /// puis quatre, puis seize avant de se declarer sur. Ce n'est pas une precaution
    /// arbitraire — deux temps confirment qu'on n'a pas rate le calage d'une croche,
    /// quatre qu'on tient la mesure, seize qu'on tient la phrase et que les deux disques
    /// ne derivent pas l'un par rapport a l'autre.
    ///
    /// Le systeme n'a aucune raison d'etre plus presse que l'oreille qui l'emploie.
    /// </summary>
    public float Trust => _agreements switch
    {
        < 2 => 0f,
        < 4 => 0.35f,
        < 16 => 0.7f,
        _ => 1f,
    };

    /// <summary>
    /// Une fenetre d'analyse.
    /// </summary>
    /// <param name="level">niveau courant, pour reperer un arret.</param>
    /// <param name="hadKick">une frappe a-t-elle ete detectee sur cette fenetre.</param>
    /// <param name="phaseError">
    /// ecart de cette frappe a la grille, en fraction de temps, deja ramene dans
    /// [-0,5 ; 0,5]. Ignore si <paramref name="hadKick"/> est faux.
    /// </param>
    public void Feed(float level, bool hadKick, float phaseError)
    {
        Broken = false;

        // L'arret : plus rien ne sort. Un blanc entre deux disques compte, et c'est voulu —
        // ce qui suit ne sera pas la suite de ce qui precede.
        if (level < SilenceLevel)
        {
            if (++_quiet >= SilenceForStop)
            {
                Break("silence", silence: true);
                _quiet = 0;
            }

            return;
        }

        _quiet = 0;
        if (!hadKick) return;

        var jumped = !_hasPrevious || MathF.Abs(phaseError - _previousError) > StrayJump;
        _previousError = phaseError;
        _hasPrevious = true;

        var stray = MathF.Abs(phaseError) > StrayPhase;

        // TROIS CAS, ET LE TROISIEME EST LE PLUS IMPORTANT.
        if (stray && jumped)
        {
            // Loin de la grille et sans rapport avec la frappe precedente : aucun geste
            // ne produit cela.
            _disorder += 1f;
            _agreements = 0;

            if (_disorder >= DisorderForBreak)
            {
                Break("frappes hors grille");
                _disorder = 0f;
            }
        }
        else if (!stray && !jumped)
        {
            // Sur la grille et dans la continuite de la precedente : tout va bien. On
            // efface plus vite qu'on n'accumule, pour que le bruit ordinaire de detection
            // — quelques frappes egarees noyees dans des justes — ne monte jamais.
            _disorder = MathF.Max(0f, _disorder - 0.75f);
            _agreements++;
        }
        else
        {
            // AMBIGU, ET ON NE TRANCHE PAS. Une frappe proche de la grille mais sans
            // rapport avec la precedente arrive tout le temps apres un saut, par simple
            // coincidence — la creer comme une preuve de bonne sante effacerait le
            // desordre aussi vite qu'il s'accumule. Une frappe loin de la grille mais
            // dans la continuite est un geste en cours. Ni l'une ni l'autre ne prouve
            // quoi que ce soit : on ne bouge pas.
            _agreements = 0;
        }
    }

    /// <summary>Apres une rupture, on repart de zero.</summary>
    public void Reset()
    {
        _disorder = 0f;
        _quiet = _agreements = 0;
        _hasPrevious = false;
        Broken = false;
        WasSilence = false;
        Reason = "";
    }

    private void Break(string why, bool silence = false)
    {
        Broken = true;
        Reason = why;
        WasSilence = silence;
        _agreements = 0;
    }
}
