namespace Emotion.Signal;

/// <summary>
/// La structure longue : combien de mesures dure une phrase, et quand la suivante commence.
///
/// POURQUOI L'APPROCHE PRECEDENTE NE POUVAIT PAS MARCHER. On alignait les phrases sur le
/// detecteur de nouveaute. Mesure faite : sur un compteur de mesures libre, les ruptures
/// qu'il signale se repartissent <b>au hasard</b> — ecart de 1,1 modulo 4 la ou il
/// faudrait depasser 8. Il signale un changement toutes les deux mesures, ce qui est le
/// rythme d'un changement de timbre et non d'une section. Une bonne intuition musicale
/// posee sur un marqueur qui ne la porte pas.
///
/// CE QUI MARCHE, ET C'EST LA MEME IDEE QU'UN ETAGE PLUS BAS. Le tempo a ete resolu en
/// cherchant a quel decalage le signal ressemble le plus a lui-meme. Une phrase obeit a
/// la meme regle, un ordre de grandeur au-dessus : elle se repete, donc elle se correle
/// avec elle-meme. On autocorrele donc une signature de mesure au lieu d'une enveloppe
/// d'attaque, sur des dizaines de secondes au lieu d'une fraction de seconde.
///
/// LA SIMPLIFICATION QUI CHANGE TOUT. On ne cherche pas une periode dans un continuum :
/// on teste <b>cinq hypotheses</b>, 2, 4, 8, 16 et 32 mesures. La musique populaire n'a
/// pas de phrases de sept mesures, et se l'interdire supprime d'un coup la quasi-totalite
/// du bruit — ainsi que l'essentiel du calcul.
/// </summary>
public sealed class SectionTracker
{
    /// <summary>
    /// Les seules longueurs de phrase que la musique de ce bac emploie.
    ///
    /// <b>Deux mesures n'y figure pas, et c'est deliberе.</b> A deux mesures on ne decrit
    /// plus une phrase mais un motif de batterie — la boucle que le batteur repete a
    /// l'interieur de la phrase. Elle se correle magnifiquement, mieux que la vraie
    /// phrase, et n'apprend rien sur la structure. Mesure faite en la laissant : elle
    /// l'emportait 60 a 68 % du temps.
    /// </summary>
    public static readonly int[] Candidates = [4, 8, 16, 32];

    /// <summary>
    /// Dimensions de la signature d'une mesure : douze bandes, plus la brillance et la
    /// densite. Assez pour distinguer un couplet d'un refrain, assez peu pour qu'une
    /// mesure n'ait pas besoin d'etre identique a la precedente pour lui ressembler.
    /// </summary>
    private const int Dims = 14;

    /// <summary>
    /// Memoire, en mesures. Soixante-quatre couvrent deux phrases de trente-deux : le
    /// minimum pour que la plus longue hypothese ait quelque chose a comparer.
    /// </summary>
    private const int History = 64;

    private const float Inertia = 0.8f;

    /// <summary>
    /// Part du meilleur score qu'une hypothese plus longue doit atteindre pour l'emporter.
    /// Une phrase de huit contient deux moities qui se ressemblent, donc l'hypothese
    /// « quatre » marque presque aussi bien — et c'est pourtant huit, la vraie phrase.
    /// A egalite approchee, la plus longue gagne.
    /// </summary>
    private const float LongerWins = 0.94f;

    /// <summary>
    /// Ressemblance valant certitude. Mesuree : 0,999 sur une phrase parfaitement
    /// reguliere, 0,092 sur du bruit.
    /// </summary>
    private const float DecisiveSimilarity = 0.5f;

    private readonly float[][] _bars = new float[History][];
    private readonly float[] _accumulator = new float[Dims];
    private int _samples;
    private int _write;
    private int _filled;

    private readonly float[] _score = new float[Candidates.Length];
    private readonly bool[] _valid = new bool[Candidates.Length];
    private readonly float[] _mean = new float[Dims];
    private readonly float[][] _centred = new float[History][];
    private readonly float[] _cut = new float[32];   // nouveaute par position dans la phrase

    private long _barCount;
    private long _boundary;

    /// <summary>Combien de fois de suite une autre longueur a fait mieux.</summary>
    private int _dissent;
    private int _pending = -1;

    /// <summary>Longueur de phrase retenue, en mesures.</summary>
    public int PhraseBars { get; private set; } = 8;

    /// <summary>A quel point la phrase se repete, 0 a 1.</summary>
    public float Confidence { get; private set; }

    /// <summary>Ressemblance brute de la meilleure hypothese. Pour le reglage.</summary>
    public float BestScore { get; private set; }

    /// <summary>Les quatre hypotheses en concurrence. Pour le reglage.</summary>
    public IReadOnlyList<float> Scores => _score;

    /// <summary>
    /// Rang de la mesure courante dans la phrase, cale sur la frontiere trouvee.
    ///
    /// Il se <b>deduit</b> d'un compteur absolu au lieu d'etre entretenu. Entretenu, il
    /// etait recalcule par la recherche de frontiere puis incremente dans la foulee, et
    /// tout changement de longueur de phrase le remettait a zero : le rang sautait a
    /// chaque hesitation entre quatre et huit mesures. Deduit, il ne peut pas sauter.
    /// </summary>
    public int BarInPhrase =>
        (int)(((_barCount - _boundary) % PhraseBars + PhraseBars) % PhraseBars);

    /// <summary>
    /// Mesures restantes avant la prochaine frontiere. <b>C'est la seule grandeur du
    /// projet qui regarde devant.</b> A zero, la phrase change maintenant ; a deux, elle
    /// changera dans deux mesures, qu'il se passe quoi que ce soit dans le son ou non.
    /// </summary>
    public int BarsToBoundary => PhraseBars - 1 - BarInPhrase;

    public SectionTracker()
    {
        for (var i = 0; i < History; i++)
        {
            _bars[i] = new float[Dims];
            _centred[i] = new float[Dims];
        }
    }

    /// <summary>Une fenetre d'analyse, accumulee dans la mesure en cours.</summary>
    public void Feed(ReadOnlySpan<float> bands, float centroid, float density)
    {
        for (var i = 0; i < 12 && i < bands.Length; i++) _accumulator[i] += bands[i];
        _accumulator[12] += centroid;
        _accumulator[13] += density;
        _samples++;
    }

    /// <summary>Une mesure vient de s'achever. Appele sur chaque debut de mesure.</summary>
    public void CloseBar()
    {
        if (_samples == 0) return;

        var slot = _bars[_write];
        for (var i = 0; i < Dims; i++)
        {
            slot[i] = _accumulator[i] / _samples;
            _accumulator[i] = 0f;
        }

        Normalise(slot);
        _samples = 0;
        _write = (_write + 1) % History;
        if (_filled < History) _filled++;

        _barCount++;
        Analyse();
    }

    public void Reset()
    {
        Array.Clear(_score);
        Array.Clear(_cut);
        Array.Clear(_accumulator);
        _filled = _write = _samples = 0;
        _barCount = _boundary = 0;
        _dissent = 0;
        _pending = -1;
        Confidence = 0f;
        PhraseBars = 8;
    }

    private void Analyse()
    {
        // Il faut au moins deux fois la plus longue hypothese comparable ; en dessous, la
        // reponse ne dirait que ce que la memoire permet de dire.
        if (_filled < 8) return;

        // CENTRER, ET CE N'EST PAS UN DETAIL — la lecon est deja ecrite un etage plus bas,
        // pour l'autocorrelation du tempo, et elle vaut ici mot pour mot.
        //
        // Une signature de mesure est faite de grandeurs positives. Sans retrancher le
        // profil moyen du passage, deux mesures quelconques se ressemblent fortement quel
        // que soit leur role : le cosinus vaut neuf dixiemes partout et ne discrimine
        // rien. Mesure de cet oubli : la longueur de phrase oscillait entre quatre et huit
        // mesures, 57 % contre 42 %, sur un passage qui n'en a qu'une.
        //
        // Centre, le cosinus ne compare plus des sons mais des <b>ecarts au son moyen</b>,
        // c'est-a-dire ce qui fait qu'une mesure joue un role plutot qu'un autre.
        Array.Clear(_mean);
        for (var k = 0; k < _filled; k++)
        {
            var b = Bar(k);
            for (var d = 0; d < Dims; d++) _mean[d] += b[d];
        }
        for (var d = 0; d < Dims; d++) _mean[d] /= _filled;

        for (var k = 0; k < _filled; k++)
        {
            var b = Bar(k);
            var c = _centred[k];
            for (var d = 0; d < Dims; d++) c[d] = b[d] - _mean[d];
        }

        var best = -1;
        var bestScore = 0f;
        Array.Fill(_valid, false);

        for (var c = 0; c < Candidates.Length; c++)
        {
            var lag = Candidates[c];

            // Une hypothese sans assez de mesures n'est pas une hypothese a zero, c'est
            // une hypothese <b>absente</b>. Lui donner zero la faisait gagner, puisque les
            // scores reels sont negatifs — et le suivi rendait alors sa valeur par defaut
            // en la faisant passer pour une mesure.
            if (_filled < lag * 2) { _valid[c] = false; continue; }
            _valid[c] = true;

            // Ressemblance moyenne entre chaque mesure et celle qui la precede d'une
            // phrase entiere. Si l'hypothese est juste, les deux jouent le meme role dans
            // leur phrase respective et se ressemblent donc fortement.
            var sum = 0f;
            var n = 0;
            for (var k = 0; k < _filled - lag; k++)
            {
                sum += Cosine(_centred[k], _centred[k + lag]);
                n++;
            }

            var fresh = n > 0 ? sum / n : 0f;
            _score[c] = _score[c] * Inertia + fresh * (1f - Inertia);

            if (best < 0 || _score[c] > bestScore) { bestScore = _score[c]; best = c; }
        }

        if (best < 0) { Confidence = 0f; return; }

        // LES SCORES SONT NEGATIFS, ET C'EST NORMAL.
        //
        // La somme de vecteurs centres est nulle par construction, donc la somme de leurs
        // produits scalaires deux a deux vaut l'oppose de la somme de leurs carres : la
        // ressemblance moyenne entre deux mesures quelconques est <b>necessairement
        // negative</b>. Un score de -0,03 n'est donc pas une absence de structure, c'est
        // un bon score si les autres valent -0,12.
        //
        // On compare donc les hypotheses entre elles, jamais a zero.
        var pool = 0f;
        var count = 0;
        for (var c = 0; c < Candidates.Length; c++)
            if (_valid[c]) { pool += _score[c]; count++; }

        var average = count > 0 ? pool / count : 0f;

        // A egalite approchee, la phrase la plus longue l'emporte : une phrase de huit
        // contient deux moities semblables, donc « quatre » marque presque aussi bien
        // alors que la phrase musicale est bien de huit. La comparaison se fait sur
        // l'avance prise au-dessus de la moyenne, seule grandeur qui ait un signe stable.
        var bestLead = bestScore - average;
        for (var c = Candidates.Length - 1; c > best; c--)
            if (_valid[c] && _score[c] - average >= bestLead * LongerWins)
            {
                best = c; bestScore = _score[c]; bestLead = bestScore - average; break;
            }

        // HYSTERESIS SUR LA LONGUEUR. Une phrase de huit contient deux moities qui se
        // ressemblent, donc l'hypothese « quatre » passe devant des que la seconde moitie
        // s'ecarte un peu. Changer a chaque hesitation ferait osciller toute la structure
        // — mesure : 54 % du temps en huit mesures, 45 % en quatre, sur le meme passage.
        // Une autre longueur doit donc gagner trois mesures de suite pour s'imposer.
        var chosen = Candidates[best];
        if (chosen != PhraseBars)
        {
            if (chosen == _pending && ++_dissent >= 3)
            {
                PhraseBars = chosen;
                _dissent = 0;
            }
            else if (chosen != _pending) { _pending = chosen; _dissent = 1; }
        }
        else { _dissent = 0; _pending = -1; }

        BestScore = bestScore;

        // LA CONFIANCE SE LIT SUR LA VALEUR ABSOLUE, ET DEUX AUTRES MESURES ONT ECHOUE
        // AVANT DE L'ETABLIR.
        //
        // Rapportee a la moyenne des hypotheses, elle donnait 1,00 sur du bruit : une
        // hypothese l'emporte toujours, par le seul jeu du hasard, et son avance parait
        // alors decisive. Rapportee a l'ecart au poursuivant, elle donnait 0,00 sur une
        // phrase parfaitement reguliere — parce que seize mesures est le double de huit
        // et se correle donc presque autant : la bonne reponse et son harmonique se
        // tiennent, ce qui ecrase l'ecart au suivant precisement quand tout va bien.
        //
        // Mesuree, la ressemblance vaut 0,999 sur une phrase franche et 0,092 sur du
        // bruit. Un facteur dix : la valeur brute separe seule, et sans artifice.
        Confidence = Math.Clamp(bestScore / DecisiveSimilarity, 0f, 1f);

        FindBoundary(chosen);
    }

    /// <summary>
    /// Ou commence la phrase. On ne la cherche pas la ou ca se ressemble, mais la ou ca
    /// <b>change</b> : une frontiere est par definition l'endroit ou une mesure ressemble
    /// le moins a la precedente, et cet endroit revient a chaque phrase.
    /// </summary>
    private void FindBoundary(int phrase)
    {
        Array.Clear(_cut, 0, _cut.Length);

        for (var k = 1; k < _filled; k++)
        {
            var change = 1f - Cosine(_centred[k - 1], _centred[k]);
            _cut[k % phrase] += change;
        }

        var at = 0;
        var most = -1f;
        for (var d = 0; d < phrase; d++)
            if (_cut[d] > most) { most = _cut[d]; at = d; }

        // La frontiere est exprimee dans le compteur absolu, pour que le rang s'en deduise
        // sans jamais etre reecrit.
        _boundary = _barCount - (_filled - 1 - at);
    }

    private ReadOnlySpan<float> Bar(int k) =>
        _bars[(_write - _filled + k + History * 2) % History];

    private static void Normalise(float[] v)
    {
        var norm = 0f;
        for (var i = 0; i < v.Length; i++) norm += v[i] * v[i];
        norm = MathF.Sqrt(norm);
        if (norm < 1e-6f) return;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    /// <summary>
    /// Cosinus de deux vecteurs quelconques. Les signatures brutes sont normalisees a
    /// l'ecriture, mais leurs versions centrees ne le sont plus — il faut donc diviser.
    /// </summary>
    private static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0f, na = 0f, nb = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var denom = MathF.Sqrt(na * nb);
        return denom < 1e-9f ? 0f : dot / denom;
    }
}
