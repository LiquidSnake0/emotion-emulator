namespace Emotion.Signal;

/// <summary>
/// La grille metrique : temps, mesure, phrase.
///
/// LE PROBLEME. Le projet savait deja ou tombent les temps, et rien de plus.
/// <see cref="TempoEstimator.Phase"/> rend bien une position sur quatre temps, mais son
/// origine est la <b>derniere attaque detectee</b> : le zero n'y designe donc pas le temps
/// fort, il designe le dernier coup entendu, quel qu'il soit. Une grille dont l'origine
/// est arbitraire ne dit rien de la structure.
///
/// CE QU'IL FAUT. Une grille qui avance toute seule et qu'on <b>corrige</b> au lieu de la
/// redemarrer, puis un vote pour decider lequel de ses quatre temps est le « 1 ».
///
/// COMMENT ON TROUVE LE « 1 ». Pas en cherchant le coup le plus fort — ce serait le kick,
/// et dans quantite de morceaux il tombe sur les quatre temps. On accumule des indices
/// convergents, chacun faible mais lisible ensemble :
///
///   le clap est sur les temps 2 et 4    l'indice le plus fiable de la musique populaire,
///                                       mais il laisse une ambiguite d'un demi-mesure
///   le kick est sur le 1                faible seul, il leve cette ambiguite
///   l'accord change sur le 1            rare, mais presque jamais ailleurs
///
/// <b>Aucun ne suffit, et deux ne suffisent pas toujours.</b> Kick sur 1 et 3 avec clap
/// sur 2 et 4 — le motif le plus repandu qui soit — est rigoureusement symetrique par
/// decalage de deux temps : aucune methode au monde n'y distingue le 1 du 3, parce que
/// l'information n'est pas dans le signal. Seul un indice de <i>plus longue portee</i>
/// tranche : un changement d'accord, une rupture de section. Tant qu'il n'est pas venu,
/// la grille rend -1 plutot que de tirer a pile ou face.
///
/// Leur accord suffit. C'est le meme principe que l'estimation du
/// tempo — <b>on vote, on ne moyenne pas</b> — et pour la meme raison : une position
/// metrique est une grandeur discrete, et la moyenne de deux hypotheses incompatibles
/// n'est pas une hypothese intermediaire, c'est une erreur.
/// </summary>
public sealed class BeatGrid
{
    /// <summary>Fraction de l'ecart corrigee a chaque detection. Voir <see cref="Sync"/>.</summary>
    private const float Pull = 0.20f;

    /// <summary>
    /// Oubli du vote, par temps. Volontairement lent : <b>le temps fort est une propriete
    /// du morceau, pas de l'instant</b>. Il ne change pas en cours de route, et une
    /// memoire courte reviendrait a jeter les indices rares — un changement d'accord, une
    /// rupture de section — avant que le suivant n'arrive, c'est-a-dire a ne garder que
    /// la batterie, qui justement ne tranche pas. A 0,99 la memoire porte sur une
    /// vingtaine de mesures. C'est <see cref="Reset"/> qui efface, au changement de
    /// disque, et lui seul.
    /// </summary>
    private const float ScoreDecay = 0.99f;
    /// <summary>
    /// Ecart de voix valant certitude. Trois votes forts separant la meilleure hypothese
    /// de sa suivante suffisent a nommer le temps fort.
    /// </summary>
    private const float DecisiveGap = 6f;

    private const float LockedAbove = 0.35f;

    // LA PHASE S'ACCUMULE, ELLE NE SE RECALCULE PAS DEPUIS UNE ORIGINE.
    //
    // La premiere version gardait l'instant du temps zero et refaisait le quotient a
    // chaque image. C'est juste tant que la periode ne bouge pas — et faux des qu'elle
    // bouge, parce que l'origine s'eloigne : a cent secondes de la, passer de 690 a
    // 620 ms fait sauter le rang de seize temps d'un coup. Le defaut dormait tant que le
    // tempo n'etait publie que sur 4 % des fenetres ; il a saute aux yeux des que
    // l'autocorrelation l'a rendu vivant, l'intervalle entre mesures tombant a 149 ms.
    //
    // En accumulant, un changement de periode ne change que la vitesse a venir. Le passe
    // reste ce qu'il etait, ce qui est la moindre des choses pour un compteur.
    private double _phase;
    private long _lastMs = -1;
    private float _beatMs = 690f;
    private long _index;             // rang du temps courant

    // Les quatre hypotheses de temps fort, en concurrence permanente.
    private readonly float[] _score = new float[4];
    private int _offset;

    public float BeatMs => _beatMs;
    public float Confidence { get; private set; }
    public bool Locked => Confidence > LockedAbove;

    public bool BarStart { get; private set; }

    /// <summary>Rang du temps courant dans la grille, non corrige du temps fort.</summary>
    public long BeatIndex => _index;

    /// <summary>Position dans le temps courant, 0 a 1.</summary>
    public float Phase { get; private set; }

    /// <summary>
    /// Ecart de la derniere frappe a la grille, en fraction de temps, dans
    /// [-0,5 ; 0,5]. C'est sur sa persistance dans le desordre — jamais sur son
    /// amplitude — que <see cref="ContinuityWatch"/> juge d'une rupture.
    /// </summary>
    public float LastSyncError { get; private set; }

    /// <summary>Rang du temps dans la mesure, ou -1 tant que le temps fort est incertain.</summary>
    public int Beat => Locked ? Mod4(_index - _offset) : -1;

    /// <summary>Les quatre hypotheses en concurrence. Pour l'ecran de reglage.</summary>
    public IReadOnlyList<float> Scores => _score;

    /// <summary>
    /// Avance la grille jusqu'a l'instant donne et signale les franchissements.
    /// </summary>
    /// <returns>vrai si un temps vient d'etre franchi.</returns>
    public bool Advance(long tMs, float? bpm)
    {
        BarStart = false;

        if (bpm is { } b && b > 40f && b < 200f)
        {
            // La periode suit le tempo mesure, mais lentement. Un tempo qui saute d'une
            // fenetre a l'autre ferait sursauter toute la grille, et le compteur de
            // mesures avec elle.
            var target = 60_000f / b;
            _beatMs += (target - _beatMs) * 0.05f;
        }

        if (_lastMs < 0) { _lastMs = tMs; return false; }

        var dt = tMs - _lastMs;
        _lastMs = tMs;
        if (dt <= 0) return false;

        _phase += dt / _beatMs;
        if (_phase < 1.0) { Phase = (float)_phase; return false; }

        // Un retard de plusieurs temps — un onglet revenu d'arriere-plan, une source qui
        // reprend — ne doit pas etre rattrape temps par temps : on rejouerait des
        // transitions qui n'ont jamais eu lieu. On saute, en n'en comptant qu'une.
        var crossed = (int)Math.Min(4, Math.Floor(_phase));
        _phase -= Math.Floor(_phase);
        Phase = (float)_phase;

        Forget();
        _index += crossed;

        // Le rang de la mesure dans la phrase ne se compte plus ici : il se mesure, et
        // c'est SectionTracker qui le tient. Deux compteurs pour une meme grandeur se
        // seraient contredits des la premiere phrase mal devinee.
        if (Beat == 0) BarStart = true;

        return true;
    }

    /// <summary>
    /// Une attaque reelle vient de tomber : on rapproche la grille sans s'y coller.
    ///
    /// Se caler entierement sur chaque detection reviendrait a n'avoir aucune grille —
    /// on retomberait sur une phase dont l'origine est le dernier coup entendu, c'est-a-dire
    /// exactement le defaut qu'on corrige. En deplacant l'origine d'un cinquieme de
    /// l'ecart, la grille converge en quelques temps et absorbe sans broncher une
    /// detection isolee qui tombe a cote.
    /// </summary>
    public void Sync(long tMs)
    {
        if (_lastMs < 0) { _lastMs = tMs; return; }

        // Une detection a 0,95 est en avance de 0,05 sur le temps suivant, pas en retard
        // de 0,95 sur le precedent.
        var error = _phase;
        if (error > 0.5) error -= 1.0;

        LastSyncError = (float)error;
        _phase -= error * Pull;
        if (_phase < 0) _phase += 1.0;
        Phase = (float)_phase;
    }

    /// <summary>
    /// Un kick. Faible indice seul — dans bien des morceaux il tombe sur les quatre temps
    /// et n'apprend alors rien — mais c'est lui qui tranche entre les deux hypotheses que
    /// le clap laisse ouvertes.
    /// </summary>
    public void MarkKick(long tMs) => Vote(Near(tMs), 1.0f);

    /// <summary>
    /// Un clap. Il vaut davantage que le kick parce qu'il est plus rare et donc plus
    /// informatif, mais il ne distingue pas le 2 du 4 : il repartit sa voix sur les deux
    /// hypotheses correspondantes, et l'ambiguite reste entiere jusqu'a ce qu'autre chose
    /// la leve.
    /// </summary>
    public void MarkClap(long tMs)
    {
        var n = Near(tMs);
        Vote(n - 1, 1.4f);
        Vote(n - 3, 1.4f);
    }

    /// <summary>
    /// Un changement d'accord. L'indice le plus decisif, et le plus rare : ailleurs que
    /// sur un temps fort, il est negligeable.
    /// </summary>
    public void MarkChange(long tMs) => Vote(Near(tMs), 2.0f);

    /// <summary>
    /// Le profil des quatre temps a designe un temps fort. Ce vote-la ne vient pas d'un
    /// evenement mais de ce que les temps <b>portent</b> en moyenne, et c'est le seul qui
    /// sache distinguer le 1 du 3 quand le kick tombe partout — par son amplitude, la ou
    /// une detection binaire ne voit que quatre kicks identiques.
    ///
    /// Il n'intervient qu'une fois par mesure : c'est une moyenne lente, la republier a
    /// chaque temps reviendrait a lui donner quatre voix pour une seule observation.
    /// </summary>
    public void MarkProfile(int offset, float weight) => Vote(offset, weight);

    /// <summary>
    /// Une rupture structurelle vient d'etre entendue. Elle ne recale plus la phrase —
    /// mesure faite, ces ruptures se repartissent au hasard sur un compteur libre et ne
    /// marquent donc rien — mais elle reste un excellent indice du temps fort : une
    /// section ne commence jamais au milieu d'une mesure.
    /// </summary>
    public void MarkSection(long tMs)
    {
        // Et elle vote. Une section ne commence jamais au milieu d'une mesure : c'est,
        // avec le changement d'accord, l'un des deux seuls indices capables de lever
        // l'ambiguite de deux temps que le backbeat laisse ouverte.
        Vote(Near(tMs), 2.0f);
    }

    /// <summary>
    /// Attenue l'acquis sans l'effacer. Employe quand la continuite parait rompue : ce
    /// qu'on croyait savoir devient douteux, il ne devient pas faux.
    /// </summary>
    public void Weaken(float factor)
    {
        for (var i = 0; i < 4; i++) _score[i] *= factor;
        Conclude();
    }

    /// <summary>
    /// Recale la phase sur une position venue d'ailleurs — celle que
    /// <see cref="PhaseFold"/> lit dans le repli de l'energie du medium.
    ///
    /// POURQUOI CE SECOND CHEMIN EXISTE A COTE DE <see cref="Sync"/>.
    ///
    /// <see cref="Sync"/> se cale sur les frappes detectees. Or moins d'une sur deux tombe
    /// sur un multiple du temps, et une boucle qui se corrige autant sur le bruit que sur
    /// le signal poursuit le bruit : confrontee a une verite terrain exterieure, la phase
    /// de cette grille tombe a 190 ms du vrai temps la ou un tirage au sort en donnerait
    /// 172. Elle est au hasard, et sa periode est pourtant juste.
    ///
    /// L'accumulateur, lui, ne depend d'aucune detection : il empile l'energie du medium
    /// sur la periode connue et regarde ou elle se concentre. 69 ms au lieu de 190.
    ///
    /// LA CORRECTION EST PONDEREE PAR LE RELIEF, ET C'EST INDISPENSABLE. Un passage sans
    /// percussion — une nappe, une intro — n'accumule rien de saillant : son histogramme
    /// est plat et son sommet ne designe qu'un accident. Le laisser tirer la grille
    /// reviendrait a la deplacer au hasard pendant les seuls moments ou elle a besoin de
    /// tenir seule.
    /// </summary>
    /// <param name="phase">Position dans le temps, 0 a 1, vue de l'exterieur.</param>
    /// <param name="poids">Ce qu'on lui accorde, 0 a 1.</param>
    public void Recaler(float phase, float poids)
    {
        if (poids <= 0f) return;

        var error = _phase - phase;
        if (error > 0.5) error -= 1.0;
        else if (error < -0.5) error += 1.0;

        _phase -= error * Math.Clamp(poids, 0f, 1f) * Pull;
        if (_phase < 0) _phase += 1.0;
        else if (_phase >= 1) _phase -= 1.0;
        Phase = (float)_phase;
    }

    public void Reset()
    {
        _phase = 0;
        _lastMs = -1;
        _index = 0;
        Array.Clear(_score);
        Confidence = 0f;
    }

    /// <summary>
    /// Rang du temps de grille le plus proche d'un instant.
    ///
    /// <b>Le plus proche, et non celui en cours.</b> Une frappe a 0,95 de phase appartient
    /// au temps suivant, pas a celui qui s'acheve. Compter les indices « pour le temps
    /// courant » puis les depouiller au franchissement paraissait naturel et se revele
    /// faux : le kick tombe justement <i>sur</i> le temps, donc a quelques millisecondes
    /// de la frontiere, et le moindre flottement de la grille le faisait basculer d'un
    /// temps a l'autre au hasard. Le vote se brouillait tout seul. En arrondissant a
    /// l'instant meme de la frappe, la question ne se pose plus.
    /// </summary>
    private long Near(long tMs) => _index + (_phase >= 0.5 ? 1 : 0);

    private void Vote(long beatIndex, float weight)
    {
        _score[Mod4(beatIndex)] += weight;
        Conclude();
    }

    private void Forget()
    {
        for (var i = 0; i < 4; i++) _score[i] *= ScoreDecay;
        Conclude();
    }

    private void Conclude()
    {
        var best = 0; var bestVal = _score[0]; var second = float.MinValue;
        for (var i = 1; i < 4; i++)
            if (_score[i] > bestVal) { second = bestVal; bestVal = _score[i]; best = i; }
            else if (_score[i] > second) second = _score[i];

        if (bestVal <= 0f) { Confidence = 0f; return; }

        // LA CONFIANCE PORTE SUR L'ECART ABSOLU, PAS SUR SA PART DU TOTAL.
        //
        // La version relative — l'ecart divise par le meilleur score — paraissait
        // raisonnable et se revele fausse, pour une raison qui vaut d'etre retenue : elle
        // laisse les indices <b>non discriminants</b> noyer ceux qui discriminent.
        //
        // Un backbeat regulier donne exactement la meme voix aux hypotheses « le 1 est
        // ici » et « le 1 est deux temps plus loin ». Il n'apprend donc rien sur laquelle
        // choisir — mais il gonfle les deux scores sans fin. Une rupture de section, elle,
        // ne vote que pour une seule : c'est la seule preuve du lot. Mesuree en relatif,
        // cette preuve unique valait cinq centiemes de confiance, noyee sous une masse
        // commune qui ne prouvait rien.
        //
        // L'ecart entre les deux meilleures hypotheses <i>est</i> la somme des votes
        // discriminants. C'est lui, et lui seul, qui porte l'information.
        _offset = best;
        Confidence = Math.Clamp((bestVal - Math.Max(second, 0f)) / DecisiveGap, 0f, 1f);
    }

    private static int Mod4(long v) => (int)(((v % 4) + 4) % 4);
}
