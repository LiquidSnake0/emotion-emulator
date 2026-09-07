namespace Emotion.Signal;

/// <summary>
/// Deduit le tempo des ecarts entre attaques, par vote plutot que par moyenne.
///
/// Une moyenne serait detruite par une seule attaque manquee, qui doublerait un ecart.
/// Un vote sur des intervalles arrondis laisse au contraire les erreurs se disperser
/// pendant que la bonne valeur s'accumule.
///
/// <b>Le tempo n'est jamais necessaire.</b> Il reste nul tant qu'il n'est pas sur, et
/// le renderer sait tourner sans lui : c'est ce qui permet de projeter des les
/// premieres secondes d'un morceau, et de ne rien casser pendant un fondu ou les
/// attaques de deux disques se melangent.
/// </summary>
public sealed class TempoEstimator
{
    private const int Keep = 24;             // ecarts conserves
    private const float MinBpm = 60f;
    private const float MaxBpm = 180f;

    /// <summary>
    /// Plage ou le tempo est repli quand c'est possible. Elle decrit <b>le repertoire</b>,
    /// pas un morceau : le crate vit entre 82 et 97 BPM, la fourchette est elargie pour
    /// laisser de la marge au fader.
    ///
    /// C'est ainsi qu'on leve l'ambiguite d'octave sans jamais lire le tempo d'une fiche.
    /// 87 et 174 produisent les memes intervalles si une frappe sur deux est plus
    /// marquee ; en preferant la valeur lente plausible, on tranche a partir du seul
    /// signal. Et la valeur rendue reste celle qui a ete mesuree, divisee, jamais celle
    /// qu'un fichier aurait annoncee.
    /// </summary>
    // LA PLAGE PREFEREE DOIT COUVRIR UN FACTEUR DEUX EXACTEMENT.
    //
    // Elle valait 70 a 110, soit un rapport de 1,57. Une plage plus etroite qu'une octave
    // laisse des valeurs sans aucun representant : 133 BPM divise par deux donne 66,5, qui
    // est sous 70, donc on le remultiplie par deux et l'on retombe sur 133. Le repli
    // n'avait pas de point fixe et la valeur oscillait entre deux bornes sans jamais
    // rentrer. Mesure sur un set : le tempo n'etait publie que sur 4 % des fenetres.
    private const float PreferredLow = 70f;
    private const float PreferredHigh = 140f;

    // Zone de confort du repertoire, elle bien plus etroite : le bac vit entre 82 et 97.
    private const float ComfortLow = 78f;
    private const float ComfortHigh = 112f;

    private readonly List<long> _gaps = new();
    private long _lastOnset = -1;
    private long _anchor = -1;               // derniere attaque retenue, origine de la phase

    public float? Bpm { get; private set; }

    /// <summary>
    /// Reprend le tempo trouve par un autre analyseur, comme point de depart et non
    /// comme verite.
    ///
    /// C'est le passage de relais d'une transition : le cue a eu huit ou seize mesures
    /// pour accrocher le tempo du disque a venir, le master n'a pas a refaire ce travail
    /// pendant le passage le plus visible du set.
    ///
    /// Mais <b>le master doit continuer a chercher</b>, et cette nuance est le coeur de
    /// la methode. Pendant le beatmatch le pitch a bouge — c'est meme le but du geste —
    /// donc le tempo du cue n'est deja plus tout a fait celui qui sort en salle. L'EQ de
    /// la table modifie en outre le spectre entre le casque et la sortie. On amorce donc
    /// le vote avec quelques ecarts au tempo repris, sans les figer : les attaques
    /// reelles du master les remplaceront en une poignee de mesures, et la valeur
    /// convergera vers ce qui joue vraiment.
    /// </summary>
    public void Adopt(float bpm, long tMs)
    {
        if (bpm < MinBpm || bpm > MaxBpm) return;

        var gap = (long)MathF.Round(60_000f / bpm);

        // Un tiers de la memoire, pas plus : assez pour donner une valeur tout de suite,
        // assez peu pour que le vote bascule des que le master parle.
        _gaps.Clear();
        for (var i = 0; i < Keep / 3; i++) _gaps.Add(gap);

        _lastOnset = tMs;
        _anchor = tMs;
        Vote();
    }

    /// <summary>Enregistre une attaque a l'instant donne.</summary>
    public void Mark(long tMs)
    {
        _anchor = tMs;

        if (_lastOnset >= 0)
        {
            var gap = tMs - _lastOnset;
            var bpm = 60_000f / gap;

            // On ne garde que des ecarts musicalement plausibles : un ecart aberrant
            // ne doit pas polluer le vote.
            if (bpm >= MinBpm && bpm <= MaxBpm)
            {
                _gaps.Add(gap);
                if (_gaps.Count > Keep) _gaps.RemoveAt(0);
                Vote();
            }
        }

        _lastOnset = tMs;
    }

    /// <summary>
    /// Position dans la mesure de quatre temps, ou nul si le tempo n'est pas accroche.
    /// Calculee depuis la derniere attaque plutot que depuis le demarrage : elle se
    /// recale donc toute seule a chaque frappe, au lieu de deriver.
    /// </summary>
    public float? Phase(long tMs)
    {
        if (Bpm is not { } bpm || _anchor < 0) return null;
        var beatMs = 60_000f / bpm;
        var beats = (tMs - _anchor) / beatMs;
        var m = beats % 4f;
        if (m < 0) m += 4f;
        return m / 4f;
    }

    /// <summary>
    /// Vote sur les ecarts arrondis a dix millisecondes. Il faut au moins un tiers des
    /// ecarts d'accord pour declarer un tempo : en dessous, on prefere ne rien dire.
    /// </summary>
    private void Vote()
    {
        if (_gaps.Count < 6) return;

        var buckets = new Dictionary<long, int>();
        foreach (var g in _gaps)
        {
            var k = (g + 5) / 10 * 10;
            buckets[k] = buckets.GetValueOrDefault(k) + 1;
        }

        var best = 0L;
        var bestCount = 0;
        foreach (var (k, c) in buckets)
            if (c > bestCount) { bestCount = c; best = k; }

        Bpm = bestCount * 3 >= _gaps.Count && best > 0
            ? Fold(60_000f / best)
            : null;
    }

    /// <summary>
    /// Ramene un tempo dans la plage du repertoire en le divisant ou le multipliant par
    /// deux. Un tempo deja plausible n'est pas touche.
    /// </summary>
    private static float Fold(float bpm)
    {
        for (var i = 0; i < 4 && bpm > PreferredHigh; i++) bpm /= 2f;
        for (var i = 0; i < 4 && bpm < PreferredLow; i++) bpm *= 2f;

        // LE REPLI TERNAIRE, et c'est un reglage de repertoire.
        //
        // Le barber beats et une bonne part du hip-hop sont joues en swing : la
        // subdivision n'est pas la croche mais le triolet, et le detecteur d'attaques y
        // voit tres bien un intervalle valant les deux tiers du temps. Mesure sur un set
        // reel : 133 BPM publies avec constance, soit exactement trois demi-temps pour
        // deux — le morceau est a 89.
        //
        // Un repli d'octave seul ne peut rien pour ce cas : 133 et 89 ne different pas
        // d'un facteur deux. On tente donc les deux tiers, et <b>seulement</b> s'ils
        // ramenent dans la zone du repertoire — a defaut on garderait la valeur brute
        // plutot que d'inventer un tempo qui arrange.
        if (bpm > ComfortHigh)
        {
            var ternary = bpm * 2f / 3f;
            if (ternary >= ComfortLow && ternary <= ComfortHigh) return ternary;
        }

        return bpm;
    }
}
