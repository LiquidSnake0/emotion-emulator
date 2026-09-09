namespace Emotion.Signal;

/// <summary>
/// Ou tombe le temps, par repli de l'energie sur la periode connue.
///
/// LE PROBLEME QU'ELLE RESOUT, ET IL ETAIT LE PLUS GRAVE DU PROJET.
///
/// <see cref="BeatGrid"/> tient une phase qui s'accumule et se corrige a chaque kick
/// detecte. Sa periode est juste — a un ou deux pour cent de celle du disque. Sa phase, en
/// revanche, est <b>au niveau du hasard</b> : confrontee a une verite terrain exterieure
/// sur dix morceaux du bac, elle tombe a 190 ms du vrai temps quand un tirage au sort en
/// donnerait 172.
///
/// La cause est connue et documentee : moins d'une frappe detectee sur deux tombe sur un
/// multiple du temps, et une boucle qui se corrige autant sur le bruit que sur le signal
/// poursuit le bruit. Quatre tentatives de fenetre de capture ont echoue pour une raison
/// de principe — une boucle a verrouillage de phase ne peut pas s'accrocher tant qu'elle
/// est mal calee, puisque toutes les vraies frappes lui paraissent alors lointaines.
///
/// CE QUI EST DIFFERENT ICI : ON N'ACCROCHE RIEN.
///
/// On empile l'energie dans un histogramme de phase et l'on regarde ou elle s'accumule.
/// Toutes les phases candidates sont examinees a chaque instant ; il n'y a pas d'etat a
/// faire converger, donc pas de phase d'acquisition, donc pas le defaut qui a fait echouer
/// les quatre tentatives precedentes. La periode, elle, est deja connue : le crate l'amorce
/// des la premiere seconde, et l'autocorrelation la tient a moins d'un pour cent.
///
/// ET SURTOUT, ON NE LA CHERCHE PAS DANS LE REGISTRE DU KICK.
///
/// C'est la mesure qui l'impose, et elle contredit l'intuition du projet. Repliee sur dix
/// morceaux du bac, l'energie designe le temps avec une erreur mediane de :
///
///     grave  0-160 Hz      231 ms      (le hasard en donne 172)
///     0-400 Hz             114 ms
///     medium 160-2000 Hz    60 ms
///     tout le spectre      104 ms
///
/// <b>Le registre du kick est le pire endroit ou chercher la phase du temps.</b> Il reste
/// le meilleur pour decider qu'une attaque a lieu — la concentration des frappes du
/// detecteur de kick vaut 0,288 contre 0,165 pour celles du clap. Les deux questions
/// « qu'est-ce qui frappe » et « ou est le temps » ne se posent pas a la meme bande, et le
/// projet n'avait pose que la premiere.
///
/// IL CHERCHE SA PROPRE PERIODE, ET CE N'EST PAS UN LUXE.
///
/// Le repli suppose la periode connue, et il y est d'une sensibilite qu'on ne devine pas.
/// Mesure, erreur de phase mediane selon l'erreur de periode :
///
///     erreur de periode     0 %    1 %    2 %    4 %
///     memoire 48 temps     85 ms  112   175    183      (le hasard en donne 172)
///     memoire 12 temps    103 ms  104   128    147
///
/// <b>Deux pour cent d'erreur de periode suffisent a ramener le repli au hasard</b> : sur
/// quarante-huit temps de memoire, cela fait un temps entier de derive, et un meme temps
/// tombe dans toutes les cases tour a tour. Or le tempo du moteur est faux de deux a cinq
/// pour cent sur plusieurs morceaux du bac — c'est exactement ce qui a rendu la premiere
/// version inutile, mesuree a 165 ms.
///
/// On tient donc plusieurs histogrammes en parallele, a des periodes legerement
/// differentes, et l'on garde celui qui se concentre le mieux. Le cout est nul a cote de
/// la FFT : sept fois cent cases a quarante-sept images par seconde.
///
/// Avec la bonne periode, le repli rend 85 ms la ou la grille actuelle en donne 190 pour
/// un hasard de 172. C'est deux fois mieux, et cela reste loin d'etre parfait.
/// </summary>
public sealed class PhaseFold
{
    /// <summary>
    /// Cases de l'histogramme. Cent donnent 6,9 ms a 87 BPM, soit trois fois plus fin que
    /// la fenetre d'analyse qui les remplit : la resolution n'est donc jamais le facteur
    /// limitant, et chaque case recoit assez de matiere pour etre lisible.
    /// </summary>
    public const int Cases = 100;

    /// <summary>
    /// Memoire, en temps. Mesure sur dix morceaux : seize temps donnent 103 ms d'erreur,
    /// soixante-quatre en donnent 69. On paie cette precision en temps d'accroche, et
    /// c'est le bon cote du marche pour un disque qui dure des minutes.
    /// </summary>
    public const float DefautMemoire = 48f;

    /// <summary>
    /// Combien de periodes voisines sont tenues en parallele. Impair, pour que la periode
    /// annoncee soit au centre.
    /// </summary>
    public const int Candidats = 7;

    /// <summary>
    /// Ecart entre deux candidats voisins. Sept candidats a cinq millemes couvrent plus ou
    /// moins un et demi pour cent, ce qui englobe l'erreur habituelle du tempo mesure sans
    /// jamais permettre d'atteindre un autre niveau metrique — le plus proche est a
    /// cinquante pour cent.
    /// </summary>
    public const float PasCandidat = 0.005f;

    /// <summary>
    /// De combien les frappes doivent l'emporter pour renverser le sommet du medium.
    ///
    /// Elles n'ont pas voix egale : leur concentration vaut 0,286 contre un hasard de 0,09,
    /// ce qui est net mais loin d'etre sur. Exiger le double les empeche de renverser un
    /// sommet franc sur un accident, tout en les laissant trancher quand le medium hesite —
    /// ce qu'il fait exactement une fois sur deux, puisque temps et contretemps se
    /// ressemblent dans son histogramme.
    ///
    /// Mesure sur dix morceaux, ecart de la grille a la verite terrain, en fraction de
    /// temps (0,25 est le hasard) :
    ///
    ///     seuil        1,5     2,0     3,0     5,0     sans les frappes
    ///     moyenne     0,207   0,178   0,207   0,184        0,187
    ///     bien cales   3/10    4/10    3/10    3/10         2/10
    ///
    /// <b>LE PAYSAGE N'EST PAS MONOTONE, ET IL FAUT LE DIRE.</b> Deux est le meilleur
    /// mesure, mais un et demi et trois donnent la meme chose : la valeur exacte n'est pas
    /// determinee par cette mesure, seule sa presence l'est. Ce qui tient, c'est le nombre
    /// de morceaux bien cales — quatre au lieu de deux — et le fait que le pire cas ne se
    /// degrade pas.
    /// </summary>
    public const float AvantageFrappe = 2.0f;

    /// <summary>Reglable, pour le banc.</summary>
    public float Avantage { get; set; } = AvantageFrappe;

    /// <summary>Largeur de la fenetre ou l'on compte les frappes, en cases de part et d'autre.</summary>
    private const int Voisinage = Cases / 12;      // un douzieme de temps, 58 ms a 87 BPM

    private readonly float[][] _cases = Creer();
    private readonly float[][] _frappes = Creer();
    private readonly double[] _phases = new double[Candidats];
    private float _memoire = DefautMemoire;
    private long _lastMs = -1;
    private float _beatMs = 690f;

    private static float[][] Creer()
    {
        var t = new float[Candidats][];
        for (var i = 0; i < Candidats; i++) t[i] = new float[Cases];
        return t;
    }

    /// <summary>Facteur de periode du candidat retenu. Un vaut le tempo annonce.</summary>
    public float Facteur { get; private set; } = 1f;

    private static float FacteurDe(int i) => 1f + (i - (Candidats - 1) / 2) * PasCandidat;

    /// <summary>Ce que les frappes ont depose autour d'une case, bords replies.</summary>
    private static float Autour(float[] table, int centre)
    {
        var somme = 0f;
        for (var d = -Voisinage; d <= Voisinage; d++)
            somme += table[(centre + d + Cases) % Cases];
        return somme;
    }

    /// <summary>Memoire de l'accumulation, en temps.</summary>
    public float Memoire
    {
        get => _memoire;
        set => _memoire = Math.Clamp(value, 1f, 512f);
    }

    /// <summary>Position du temps dans le cycle, 0 a 1. Zero tant que rien n'est vu.</summary>
    public float Phase { get; private set; }

    /// <summary>
    /// A quel point l'histogramme designe un endroit plutot qu'un autre : le sommet
    /// rapporte a la moyenne, ramene entre 0 et 1. Un profil etale vaut 0.
    ///
    /// C'est elle qui dit s'il faut croire <see cref="Phase"/>. Une energie qui ne se
    /// concentre nulle part — un passage sans percussion, une nappe — ne doit pas deplacer
    /// une grille acquise.
    /// </summary>
    public float Relief { get; private set; }

    /// <summary>Combien de temps ont ete accumules. En dessous de quatre, on ne dit rien.</summary>
    public float Temps { get; private set; }

    /// <summary>
    /// La phase telle que <see cref="BeatGrid"/> la compte : zero au temps, un au suivant.
    ///
    /// <see cref="Phase"/> dit ou tombe le temps DANS LE CYCLE INTERNE de cet accumulateur,
    /// dont l'origine est arbitraire — c'est l'instant ou il a commence a compter. Ce que
    /// la grille veut savoir est autre chose : combien de temps s'est ecoule depuis le
    /// dernier temps. C'est la difference entre les deux, et la confondre decalerait la
    /// grille de la valeur meme qu'on cherche a corriger.
    /// </summary>
    public float PhaseDuTemps
    {
        get
        {
            var d = (float)_phases[_retenu] - Phase;
            return d < 0 ? d + 1f : d;
        }
    }

    private int _retenu = (Candidats - 1) / 2;

    /// <summary>
    /// Verse l'energie de cette fenetre dans l'histogramme, et relit ou tombe le temps.
    /// </summary>
    /// <param name="tMs">Instant de la fenetre.</param>
    /// <param name="energie">Ce qui monte dans le medium sur cette fenetre.</param>
    /// <param name="bpm">Tempo mesure, ou nul tant qu'il n'y en a pas.</param>
    /// <param name="frappe">Un kick vient d'etre detecte sur cette fenetre.</param>
    public void Feed(long tMs, float energie, float? bpm, bool frappe = false)
    {
        if (bpm is { } b && b > 20f && b < 400f)
        {
            // La periode suit le tempo lentement, comme partout ailleurs : un tempo qui
            // saute d'une fenetre a l'autre ferait tourner tout l'histogramme.
            var cible = 60_000f / b;
            _beatMs += (cible - _beatMs) * 0.05f;
        }

        if (_lastMs < 0) { _lastMs = tMs; return; }
        var dt = tMs - _lastMs;
        _lastMs = tMs;
        if (dt <= 0 || dt > 1000) return;

        // L'oubli s'applique par fenetre et non par temps : sinon la memoire dependrait du
        // tempo, et un disque lent serait accumule plus longtemps qu'un rapide.
        var demiVie = _memoire * _beatMs / Math.Max(1f, dt);
        var oubli = MathF.Pow(0.5f, 1f / Math.Max(1f, demiVie));
        var apport = Math.Max(0f, energie);

        var meilleurRelief = -1f;
        var meilleurRang = 0;
        var meilleur = _retenu;

        for (var c = 0; c < Candidats; c++)
        {
            // Chaque candidat a sa propre periode, donc sa propre phase : c'est la seule
            // facon de les departager, puisqu'une periode fausse etale le profil.
            var periode = _beatMs * FacteurDe(c);
            _phases[c] += dt / periode;
            var tours = Math.Floor(_phases[c]);
            _phases[c] -= tours;
            if (c == _retenu) Temps += (float)tours;

            var cases = _cases[c];
            var frappes = _frappes[c];
            var mien = (int)(_phases[c] * Cases) % Cases;
            var somme = 0f;
            var sommet = 0f;
            var rang = 0;
            for (var i = 0; i < Cases; i++)
            {
                cases[i] *= oubli;
                frappes[i] *= oubli;
                if (i == mien)
                {
                    cases[i] += apport;
                    if (frappe) frappes[i] += 1f;
                }
                somme += cases[i];
                if (cases[i] > sommet) { sommet = cases[i]; rang = i; }
            }

            if (somme <= 0f) continue;

            // LE MEDIUM NE DISTINGUE PAS LE TEMPS DU CONTRETEMPS, ET LE KICK SI.
            //
            // Replie sur un temps, l'histogramme du medium a deux sommets qui se
            // ressemblent : ce repertoire pose autant d'accords entre les temps que dessus.
            // Rien dans l'energie ne dit lequel est le « 1 », et se tromper coute un demi-
            // temps — mesure sur trois morceaux du bac, ou la grille se verrouillait avec
            // confiance a cote, ce qui est pire que de ne pas se verrouiller.
            //
            // Le kick, lui, tombe du bon cote : sa concentration vaut 0,286 pour un hasard
            // de 0,09. On lui demande donc de departager le sommet et son antipode, et
            // rien d'autre — il ne place pas la grille, il choisit entre deux positions
            // que le medium a deja trouvees.
            var antipode = (rang + Cases / 2) % Cases;
            if (Autour(frappes, antipode) > Autour(frappes, rang) * Avantage)
                rang = antipode;
            var relief = sommet / (somme / Cases);
            if (relief > meilleurRelief)
            {
                meilleurRelief = relief;
                meilleurRang = rang;
                meilleur = c;
            }
        }

        if (Temps < 4f || meilleurRelief < 0f)
        {
            Relief = 0f;
            return;
        }

        _retenu = meilleur;
        Facteur = FacteurDe(meilleur);

        // Le centre de la case, jamais son bord : prendre le bord biaiserait la phase d'une
        // demi-case, soit 3,5 ms a 87 BPM, dans un sens toujours le meme.
        Phase = (meilleurRang + 0.5f) / Cases;

        // Sommet rapporte a la moyenne. Un profil parfaitement etale donne 1, d'ou le
        // retranchement ; on sature a un relief de six, au-dela duquel il n'y a plus rien
        // a departager.
        Relief = Math.Clamp((meilleurRelief - 1f) / 5f, 0f, 1f);
    }

    /// <summary>Oublie tout : changement de disque, ou silence.</summary>
    public void Reset()
    {
        foreach (var c in _cases) Array.Clear(c);
        foreach (var c in _frappes) Array.Clear(c);
        Array.Clear(_phases);
        _lastMs = -1;
        Temps = 0f;
        Phase = 0f;
        Relief = 0f;
        Facteur = 1f;
        _retenu = (Candidats - 1) / 2;
    }
}
