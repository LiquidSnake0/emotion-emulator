namespace Emotion.Pulse;

/// <summary>
/// Mesure la pulsation d'une suite d'instants, sans consulter aucune grille du systeme.
///
/// POURQUOI CETTE MESURE MANQUAIT, ET CE QU'ELLE DEBLOQUE.
///
/// Le projet disposait de deux familles d'indicateurs, et aucune ne repondait a la seule
/// question qui compte pour le detecteur.
///
///   Les indicateurs internes — « intervalles sur la grille », « frappes bien calees »,
///   « verrouillage » — comparent les frappes a la grille, laquelle se cale sur ces memes
///   frappes. Un defaut commun aux deux leur est invisible : c'est ainsi qu'un retard
///   systematique de vingt millisecondes a survecu a des semaines de reglages.
///
///   La confrontation a une autre implementation repond « est-ce un vrai evenement »,
///   jamais « est-ce le BON evenement ». Un detecteur qui tirerait sur toutes les attaques
///   du morceau y excellerait tout en rendant la grille inutilisable — c'est exactement ce
///   qu'a fait le blanchiment adaptatif : neuf points de justesse gagnes, sept points de
///   verrouillage perdus.
///
/// Il manquait donc une mesure de la <b>pulsation</b> : ces instants forment-ils un pouls
/// regulier, et est-ce le meme pouls que celui d'une reference exterieure ?
///
/// COMMENT, SANS TOLERANCE ARBITRAIRE.
///
/// Une periode etant donnee, on replie chaque instant sur un cercle — un tour complet par
/// periode — et l'on somme les vecteurs unitaires obtenus. Si les instants tombent tous au
/// meme endroit du cycle, les vecteurs s'additionnent et la resultante vaut un ; s'ils sont
/// disperses, ils s'annulent.
///
/// C'est la statistique de Rayleigh, et elle a deux vertus ici. Elle ne demande <b>aucune
/// fenetre de tolerance</b>, donc aucun reglage a defendre ; et son niveau de hasard se
/// calcule au lieu de s'estimer — pour n instants places au hasard, la resultante vaut en
/// moyenne la racine de pi sur deux racines de n.
/// </summary>
public static class Pulsation
{
    /// <summary>Periode la plus courte exploree, en secondes. 1,10 s vaut 55 BPM.</summary>
    private const double Longue = 1.10;

    /// <summary>Periode la plus longue exploree. 0,30 s vaut 200 BPM.</summary>
    private const double Courte = 0.30;

    private const double Pas = 0.0005;

    /// <summary>
    /// Une periode trouvee, avec la force et la phase qui vont avec.
    /// </summary>
    /// <param name="Periode">En secondes.</param>
    /// <param name="Force">Resultante de Rayleigh, 0 a 1.</param>
    /// <param name="Phase">Ou tombe le temps fort, en secondes depuis l'origine.</param>
    /// <param name="Hasard">Ce que vaudrait la force pour autant d'instants au hasard.</param>
    public readonly record struct Pouls(double Periode, double Force, double Phase, double Hasard)
    {
        public double Bpm => Periode > 0 ? 60.0 / Periode : 0;

        /// <summary>La force, rapportee a ce que le hasard aurait donne.</summary>
        public double Rapport => Hasard > 0 ? Force / Hasard : 0;
    }

    /// <summary>Force de la pulsation d'une suite d'instants a une periode donnee.</summary>
    public static (double Force, double Phase) A(IReadOnlyList<double> instants, double periode)
    {
        if (instants.Count == 0 || periode <= 0) return (0, 0);

        double x = 0, y = 0;
        foreach (var t in instants)
        {
            var a = 2 * Math.PI * t / periode;
            x += Math.Cos(a);
            y += Math.Sin(a);
        }

        var n = instants.Count;
        var force = Math.Sqrt(x * x + y * y) / n;

        // L'angle de la resultante donne l'endroit du cycle ou les instants s'accumulent.
        var angle = Math.Atan2(y, x);
        if (angle < 0) angle += 2 * Math.PI;
        return (force, angle / (2 * Math.PI) * periode);
    }

    /// <summary>
    /// Cherche la periode qui rassemble le mieux ces instants.
    ///
    /// ON PREND LA PLUS LONGUE DES MEILLEURES, ET CE N'EST PAS UN DETAIL. Des instants
    /// poses sur une grille de periode P tombent aussi, exactement, sur une grille de P/2,
    /// de P/3, de P/4. La force ne peut donc que croitre quand la periode raccourcit, et un
    /// simple maximum choisirait toujours la plus courte periode exploree. On retient donc
    /// la plus longue periode dont la force atteint presque le maximum : c'est la seule qui
    /// ne soit pas une sous-division d'une autre.
    /// </summary>
    public static Pouls Chercher(IReadOnlyList<double> instants)
    {
        if (instants.Count < 8) return new Pouls(0, 0, 0, 0);

        var meilleure = 0.0;
        for (var p = Courte; p <= Longue; p += Pas)
            meilleure = Math.Max(meilleure, A(instants, p).Force);

        var seuil = meilleure * 0.97;
        var retenue = Courte;
        for (var p = Courte; p <= Longue; p += Pas)
            if (A(instants, p).Force >= seuil) retenue = p;

        // PUIS ON AFFINE, ET CE SECOND TOUR N'EST PAS UN LUXE.
        //
        // La tolerance de trois pour cent qui protege des sous-divisions coute de la
        // precision : sur un metronome exact a 682,98 ms, elle retenait 684,5 ms. Une
        // milliseconde et demie parait negligeable et ne l'est pas — sur soixante frappes
        // elle accumule 0,13 temps de derive, et l'accord tombe de 1,00 a 0,97.
        //
        // Le premier tour choisit donc la bonne OCTAVE, le second la bonne PERIODE : on
        // reprend le maximum vrai dans une bande etroite autour d'elle, ou aucune
        // sous-division ne peut se glisser.
        const double Fin = 0.00005;
        var bas = retenue * 0.97;
        var haut = retenue * 1.03;
        var exacte = retenue;
        var forceExacte = A(instants, retenue).Force;
        for (var p = bas; p <= haut; p += Fin)
        {
            var f = A(instants, p).Force;
            if (f > forceExacte) { forceExacte = f; exacte = p; }
        }

        var (force, phase) = A(instants, exacte);
        return new Pouls(exacte, force, phase, Hasard(instants.Count));
    }

    /// <summary>
    /// Cherche le pouls par fenetres glissantes, puis rend la mediane.
    ///
    /// LA MESURE GLOBALE EST TROP FRAGILE, ET LE CHIFFRE QUI L'A MONTRE EST PARLANT.
    ///
    /// Sur quatre-vingt-dix secondes d'un morceau a 87 BPM, la force vaut 0,062 a la
    /// periode de 690 ms et 0,268 a celle de 696 ms. Huit dixiemes de pour cent d'ecart, et
    /// le score s'effondre. Ce n'est pas un defaut de calcul : il y a cent trente temps
    /// dans la fenetre, donc 0,8 % d'erreur de periode accumule un temps entier de derive
    /// d'un bout a l'autre, et les vecteurs finissent par pointer partout.
    ///
    /// Autrement dit, la mesure globale exige un tempo rigoureusement constant. Un vinyle
    /// joue au fader ne l'est jamais, et meme un fichier derive un peu. Elle mesurait donc
    /// la constance du tempo autant que la regularite des frappes, ce qui n'est pas la
    /// question.
    ///
    /// ON DECOUPE DONC EN FENETRES COURTES. Une quinzaine de secondes : assez pour une
    /// vingtaine de frappes, trop peu pour qu'une derive plausible s'y accumule. Chaque
    /// fenetre trouve son propre pouls, et l'on prend la mediane — insensible aux quelques
    /// fenetres ou le morceau se tait ou change de section.
    ///
    /// LA DISPERSION DES PERIODES TROUVEES EST ELLE AUSSI UNE MESURE. Un vrai pouls donne
    /// la meme periode d'une fenetre a l'autre ; des frappes irregulieres donnent une
    /// periode differente a chaque fois, meme quand chacune parait forte isolement.
    /// </summary>
    /// <param name="fenetreS">Duree d'une fenetre, en secondes.</param>
    public static (Pouls Median, double Stabilite, int Fenetres) ChercherLocal(
        IReadOnlyList<double> instants, double fenetreS = 15.0)
    {
        if (instants.Count < 8) return (new Pouls(0, 0, 0, 0), 0, 0);

        var debut = instants[0];
        var fin = instants[^1];
        var pas = fenetreS / 2;

        var trouves = new List<Pouls>();
        for (var d = debut; d + fenetreS <= fin + pas; d += pas)
        {
            var tranche = new List<double>();
            foreach (var t in instants)
                if (t >= d && t < d + fenetreS) tranche.Add(t);
            if (tranche.Count < 8) continue;
            var p = Chercher(tranche);
            if (p.Periode > 0) trouves.Add(p);
        }

        if (trouves.Count == 0) return (new Pouls(0, 0, 0, 0), 0, 0);

        static double Med(IEnumerable<double> xs)
        {
            var l = xs.OrderBy(x => x).ToList();
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }

        var periode = Med(trouves.Select(p => p.Periode));
        var force = Med(trouves.Select(p => p.Force));
        var hasard = Med(trouves.Select(p => p.Hasard));

        // Stabilite : part des fenetres dont la periode tombe a moins de 3 % de la mediane.
        // Trois pour cent, parce qu'un vinyle se joue a plus ou moins huit au fader et
        // qu'on ne veut pas compter une correction de calage comme une irregularite.
        var stables = trouves.Count(p => Math.Abs(p.Periode - periode) / periode < 0.03);

        return (new Pouls(periode, force, 0, hasard),
                stables / (double)trouves.Count, trouves.Count);
    }

    /// <summary>
    /// Ce que la resultante vaut en moyenne pour n instants places au hasard.
    /// Racine de pi sur deux racines de n — la valeur classique pour une marche
    /// aleatoire de n pas unitaires.
    /// </summary>
    public static double Hasard(int n) => n > 0 ? Math.Sqrt(Math.PI) / (2 * Math.Sqrt(n)) : 0;

    /// <summary>
    /// A quel point ces instants tombent sur un pouls donne, de -1 a +1.
    ///
    /// LA MESURE QU'ON CHERCHAIT. Le pouls vient d'ailleurs — d'une autre implementation,
    /// ou d'un enregistrement dont on connait le tempo — donc rien ici ne se juge contre
    /// soi-meme.
    ///
    /// On rend le cosinus moyen de l'ecart au temps le plus proche, ce qui donne trois
    /// lectures franches et sans seuil :
    ///
    ///   <b>+1</b>  chaque instant tombe sur un temps
    ///    <b>0</b>  les instants sont disperses, le detecteur ne suit pas ce pouls
    ///   <b>-1</b>  chaque instant tombe exactement <b>entre</b> deux temps
    ///
    /// La derniere valeur est la plus interessante des trois : un detecteur peut etre
    /// parfaitement regulier et parfaitement a cote, et aucun indicateur precedent ne
    /// savait le dire.
    /// </summary>
    public static double Accord(IReadOnlyList<double> instants, Pouls pouls)
    {
        if (instants.Count == 0 || pouls.Periode <= 0) return 0;

        var somme = 0.0;
        foreach (var t in instants)
            somme += Math.Cos(2 * Math.PI * (t - pouls.Phase) / pouls.Periode);
        return somme / instants.Count;
    }

    /// <summary>
    /// Le rapport entre deux periodes, ramene a une fraction simple si c'en est une.
    /// Sert a distinguer « le detecteur suit un autre pouls » de « il suit le meme a
    /// l'octave pres », qui sont deux defauts tres differents.
    /// </summary>
    public static string Rapport(double periode, double reference)
    {
        if (reference <= 0 || periode <= 0) return "—";
        var r = periode / reference;
        foreach (var (num, den, nom) in Fractions)
        {
            var cible = num / (double)den;
            if (Math.Abs(r - cible) / cible < 0.04) return nom;
        }
        return $"x{r:F2}";
    }

    private static readonly (int, int, string)[] Fractions =
    [
        (1, 4, "le quart"), (1, 3, "le tiers"), (1, 2, "la moitie"),
        (2, 3, "deux tiers"), (1, 1, "le meme"), (3, 2, "une fois et demie"),
        (2, 1, "le double"), (3, 1, "le triple"), (4, 1, "le quadruple"),
    ];
}
