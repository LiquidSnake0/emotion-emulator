namespace Emotion.Signal;

/// <summary>
/// Le tempo par autocorrelation de l'enveloppe d'attaque.
///
/// POURQUOI L'ANCIENNE METHODE PLAFONNAIT. Elle votait sur les ecarts entre attaques
/// <b>consecutives</b>, et cette seule contrainte suffit a la condamner : une frappe
/// manquee double l'ecart, une frappe parasite le coupe en deux, et le vote se retrouve
/// avec un ecart qui ne correspond a rien. Elle regroupait de surcroit par cases de dix
/// millisecondes, soit 1,4 % d'un temps a 87 BPM — une frappe jouee a la main en sort en
/// permanence. Mesure sur un vrai set : <b>tempo publie sur 4 a 11 % des fenetres</b>.
/// Elargir la case fait monter la detection et rend le tempo instable, ce qui est pire :
/// absente, la grille garde sa periode et continue ; instable, elle court apres.
///
/// CE QUI CHANGE ICI. L'autocorrelation ne demande aucune decision binaire. Elle ne
/// regarde pas des attaques mais l'<b>enveloppe continue</b> du registre du kick, et
/// mesure a quel point elle ressemble a elle-meme decalee d'un certain temps. Une frappe
/// manquee ne casse rien : les autres periodes se ressemblent toujours. C'est ce que font
/// les bibliotheques du domaine, et pour cette raison-la.
///
/// TROIS PIECES, ET CHACUNE REPOND A UN DEFAUT PRECIS.
///
///   l'autocorrelation      supprime la dependance aux attaques individuelles
///   la fenetre de Rayleigh  tranche l'ambiguite d'octave sans replier a la main
///   l'inertie sur la courbe donne la stabilite que le vote n'avait pas
///
/// La derniere est la plus importante en pratique. Le score de chaque periode est
/// accumule d'une analyse a l'autre au lieu d'etre recalcule : un tempo n'est pas une
/// mesure instantanee, c'est une propriete qui dure, et le mesurer comme telle est ce qui
/// l'empeche de sauter.
/// </summary>
public sealed class TempoTracker
{
    public const float MinBpm = 60f;
    public const float MaxBpm = 200f;

    /// <summary>
    /// Duree observee. Huit secondes portent une dizaine de temps du repertoire : assez
    /// pour qu'une periode se detache du bruit, assez peu pour suivre un changement de
    /// disque sans trainer une demi-minute.
    /// </summary>
    private const float MemorySeconds = 8f;

    /// <summary>
    /// Nombre de fenetres entre deux analyses. Un tempo ne change pas en cent
    /// soixante-dix millisecondes, et recalculer a chaque image couterait huit fois plus
    /// pour rigoureusement rien.
    /// </summary>
    private const int Refresh = 8;

    /// <summary>
    /// Inertie de la courbe de score. C'est elle qui fait la stabilite : a 0,85, une
    /// periode doit convaincre pendant plus d'une seconde avant de l'emporter.
    /// </summary>
    private const float Inertia = 0.85f;

    /// <summary>
    /// Centre de la preference de tempo, en BPM. <b>Tire du repertoire.</b> Le bac vit
    /// entre 82 et 97 ; la ponderation n'interdit rien hors de cette zone, elle penche.
    /// C'est ce qui remplace le repli d'octave a la main — entre une periode et sa
    /// moitie, qui se ressemblent toutes deux fortement, c'est la preference qui tranche.
    /// </summary>
    private const float PreferredBpm = 90f;

    /// <summary>
    /// Largeur de la preference, en octaves de tempo. Un quart d'octave separe 90 BPM de
    /// 107 d'un cote et de 76 de l'autre.
    /// </summary>
    private const float PreferWidth = 0.25f;

    /// <summary>
    /// Correlation valant certitude. Le bruit produit environ 0,05 sur huit secondes
    /// d'observation, une pulsation franche trois a six dixiemes.
    /// </summary>
    private const float DecisiveCorrelation = 0.35f;

    /// <summary>
    /// Pas minimal du tempo publie, en BPM.
    ///
    /// <b>Ordre de grandeur, pas valeur mesuree.</b> Selim l'a donne de memoire — « passer
    /// de 93 a 94 ca va meme pas se sentir » — et le principe est juste : le seuil du
    /// perceptible est un ecart de tempo et non une proportion, sans quoi la grille serait
    /// plus nerveuse sur un morceau lent que sur un rapide. Le chiffre lui-meme reste a
    /// verifier a l'oreille, sur le mur.
    ///
    /// Il ne conditionne d'ailleurs aucun envoi : le paquet part a chaque instant t, quoi
    /// qu'il arrive. Ce pas ne fait que decider si la valeur publiee bouge.
    /// </summary>
    private const float TempoStep = 1f;

    private const float PublishAbove = 0.35f;

    private readonly float[] _history;
    private readonly float[] _score;
    private readonly float[] _raw;
    private readonly float[] _prefer;
    private readonly float[] _work;
    private readonly int _minLag, _maxLag;
    private readonly float _frameMs;

    private int _write;
    private int _filled;
    private int _sinceAnalysis;

    private long _anchor = -1;

    public float? Bpm { get; private set; }

    /// <summary>Nettete du pic, 0 a 1. Sous <see cref="PublishAbove"/> on ne dit rien.</summary>
    public float Confidence { get; private set; }

    /// <summary>
    /// Les meilleures periodes de la courbe, en BPM, avec leur correlation brute et leur
    /// score pondere. Pour le reglage : savoir si une hypothese est absente ou seulement
    /// battue change entierement le diagnostic.
    /// </summary>
    public IEnumerable<(float Bpm, float Raw, float Score)> Peaks(int take = 6)
    {
        var idx = Enumerable.Range(0, _score.Length).ToList();
        idx.Sort((a, b) => _score[b].CompareTo(_score[a]));

        foreach (var i in idx.Take(take))
            yield return (60_000f / ((i + _minLag) * _frameMs), _raw[i], _score[i]);
    }

    /// <summary>Correlation brute a une periode donnee, en BPM. Pour le reglage.</summary>
    public float RawAt(float bpm)
    {
        var lag = (int)MathF.Round(60_000f / bpm / _frameMs) - _minLag;
        return lag >= 0 && lag < _raw.Length ? _raw[lag] : 0f;
    }

    public TempoTracker(int sampleRate = 48_000, int window = SpectrumAnalyzer.Window)
    {
        _frameMs = window * 1000f / sampleRate;

        _minLag = Math.Max(2, (int)MathF.Floor(60_000f / MaxBpm / _frameMs));
        _maxLag = (int)MathF.Ceiling(60_000f / MinBpm / _frameMs);

        _history = new float[(int)(MemorySeconds * 1000f / _frameMs)];
        _work = new float[_history.Length];
        _score = new float[_maxLag - _minLag + 1];
        _raw = new float[_score.Length];
        _prefer = new float[_score.Length];

        // PREFERENCE LOG-NORMALE, ET NON UNE FENETRE DE RAYLEIGH.
        //
        // La Rayleigh est la ponderation classique du domaine et elle ne convient pas ici,
        // pour une raison mesurable : elle est trop plate. Sur un morceau donne a 90 BPM
        // par son proprietaire, elle accordait 0,80 a l'hypothese 128 contre 0,82 a
        // l'hypothese 85 — autant dire rien, alors que 128 est exactement le triolet de 85
        // et que le repertoire est joue en swing. Le mauvais tempo l'emportait.
        //
        // L'oreille juge les tempos en <b>rapports</b> et non en differences : entre 60 et
        // 70 il y a le meme intervalle qu'entre 120 et 140. La preference doit donc etre
        // gaussienne en logarithme du tempo, comme le sont deja les bandes et le
        // centroide dans ce projet.
        //
        // A un quart d'octave d'ecart-type, le triolet d'un tempo prefere ne pese plus
        // qu'un huitieme de lui — assez pour le battre a correlation comparable, pas assez
        // pour interdire un tempo franchement hors zone de s'imposer avec un pic net.
        for (var i = 0; i < _prefer.Length; i++)
        {
            var bpm = 60_000f / ((_minLag + i) * _frameMs);
            var octaves = MathF.Log2(bpm / PreferredBpm) / PreferWidth;
            _prefer[i] = MathF.Exp(-octaves * octaves / 2f);
        }
    }

    /// <summary>
    /// Une fenetre d'analyse. <paramref name="envelope"/> est l'enveloppe du registre du
    /// kick — la meme grandeur qui decide des frappes, et non le flux global : le souffle
    /// et le crepitement de vinyle remplissent les aigus d'energie qui n'a pas de periode.
    /// </summary>
    public void Feed(float envelope)
    {
        _history[_write] = envelope;
        _write = (_write + 1) % _history.Length;
        if (_filled < _history.Length) _filled++;

        if (++_sinceAnalysis < Refresh) return;
        _sinceAnalysis = 0;
        Analyse();
    }

    /// <summary>
    /// Une attaque : elle ne sert plus qu'a fixer l'origine des temps. La periode vient
    /// de l'enveloppe, la phase des frappes — chacune de ce qu'elle sait le mieux dire.
    /// </summary>
    public void Mark(long tMs) => _anchor = tMs;

    /// <summary>
    /// Amorce depuis le tempo d'une autre platine, au moment du relais. On ne force pas
    /// la valeur : on depose un pic sur la periode annoncee et l'autocorrelation continue
    /// de chercher. Le pitch a bouge pendant le beatmatch, et l'EQ de la table modifie le
    /// spectre — <b>c'est une amorce, pas un verrou</b>.
    /// </summary>
    public void Adopt(float bpm, long tMs)
    {
        if (bpm < MinBpm || bpm > MaxBpm) return;

        var lag = (int)MathF.Round(60_000f / bpm / _frameMs);
        var i = lag - _minLag;
        if (i < 0 || i >= _score.Length) return;

        var peak = 0f;
        foreach (var v in _raw) if (v > peak) peak = v;

        _raw[i] = MathF.Max(_raw[i], MathF.Max(peak, DecisiveCorrelation) * 1.2f);
        _score[i] = _raw[i] * _prefer[i];
        _anchor = tMs;
        Conclude();
    }

    /// <summary>Position dans la mesure, 0 a 1, depuis la derniere attaque retenue.</summary>
    public float? Phase(long tMs)
    {
        if (Bpm is not { } bpm || _anchor < 0) return null;

        var beatMs = 60_000f / bpm;
        var beats = (tMs - _anchor) / beatMs;
        var m = beats % 4f;
        if (m < 0) m += 4f;
        return m / 4f;
    }

    public void Reset()
    {
        Array.Clear(_score);
        Array.Clear(_raw);
        Array.Clear(_history);
        _filled = 0;
        _write = 0;
        Bpm = null;
        Confidence = 0f;
        _anchor = -1;
    }

    private void Analyse()
    {
        // Il faut au moins de quoi couvrir deux fois la plus longue periode testee, sans
        // quoi le decalage maximal n'aurait qu'une poignee de termes et son score serait
        // un artefact plutot qu'une mesure.
        if (_filled < _maxLag * 3) return;

        // Remettre l'historique a plat, du plus ancien au plus recent.
        var n = _filled;
        for (var i = 0; i < n; i++)
            _work[i] = _history[(_write - n + i + _history.Length * 2) % _history.Length];

        // CENTRER, ET CE N'EST PAS UN DETAIL. Une enveloppe est positive ; sans retrancher
        // sa moyenne, l'autocorrelation est dominee par cette composante continue et
        // decroit simplement avec le decalage. Tous les tempos se ressembleraient.
        var mean = 0f;
        for (var i = 0; i < n; i++) mean += _work[i];
        mean /= n;

        var energy = 0f;
        for (var i = 0; i < n; i++)
        {
            _work[i] -= mean;
            energy += _work[i] * _work[i];
        }

        if (energy < 1e-6f) return;

        for (var lag = _minLag; lag <= _maxLag; lag++)
        {
            var sum = 0f;
            for (var i = lag; i < n; i++) sum += _work[i] * _work[i - lag];

            // Normalisation par le nombre de termes puis par l'energie : sans quoi les
            // decalages courts, qui cumulent plus de produits, gagneraient toujours.
            var r = sum / ((n - lag) * (energy / n));
            var i2 = lag - _minLag;
            var fresh = MathF.Max(0f, r);

            // Deux courbes, et il faut les deux. Celle qui porte la preference sert a
            // choisir ; celle qui reste brute sert a juger si le choix vaut quelque chose.
            // Les confondre revient a mesurer sa propre preference.
            _raw[i2] = _raw[i2] * Inertia + fresh * (1f - Inertia);
            _score[i2] = _raw[i2] * _prefer[i2];
        }

        Conclude();
    }

    private void Conclude()
    {
        var best = 0;
        var bestVal = 0f;

        for (var i = 0; i < _score.Length; i++)
            if (_score[i] > bestVal) { bestVal = _score[i]; best = i; }

        if (bestVal <= 0f) { Bpm = null; Confidence = 0f; return; }

        // LA CONFIANCE PORTE SUR LA HAUTEUR DE LA CORRELATION, PAS SUR LA FORME DE LA
        // COURBE.
        //
        // Elle mesurait d'abord de combien le pic depassait la moyenne des scores. C'est
        // faux, et spectaculairement : les correlations negatives sont ramenees a zero,
        // donc sur un signal sans pulsation la moitie de la courbe vaut exactement zero,
        // la moyenne s'effondre, et la moindre fluctuation parait ecraser tout le reste.
        // Mesure sur du bruit blanc : 0,90 de confiance pour un tempo entierement invente.
        //
        // La grandeur qui distingue vraiment est absolue. Une autocorrelation normalisee
        // vaut environ un sur racine du nombre d'echantillons quand il n'y a rien, et
        // plusieurs dixiemes sur une vraie pulsation. On la lit donc telle quelle, sur la
        // courbe restee brute — juger sur la courbe ponderee reviendrait a mesurer sa
        // propre preference.
        Confidence = Math.Clamp(_raw[best] / DecisiveCorrelation, 0f, 1f);

        if (Confidence < PublishAbove) { Bpm = null; return; }

        // Interpolation parabolique sur les trois points du sommet. Le pas de decalage
        // vaut 21 ms, soit 3 % d'un temps : s'en tenir a l'entier donnerait un tempo par
        // marches, et la grille sauterait a chaque changement de marche.
        var lag = (float)(best + _minLag);
        if (best > 0 && best < _score.Length - 1)
        {
            var a = _score[best - 1];
            var b = _score[best];
            var c = _score[best + 1];
            var d = a - 2f * b + c;
            if (MathF.Abs(d) > 1e-9f) lag += 0.5f * (a - c) / d;
        }

        var bpm = 60_000f / (lag * _frameMs);
        if (bpm < MinBpm || bpm > MaxBpm) { Bpm = null; return; }

        // Hysteresis d'un BPM, en valeur absolue et non en pourcentage.
        //
        // C'est la formulation de Selim, et elle est meilleure : « passer de 93 a 94 ca va
        // meme pas se sentir ». Le seuil du perceptible est un ecart de tempo, pas une
        // proportion — un pourcentage rendrait la grille plus nerveuse sur un morceau lent
        // que sur un morceau rapide, alors que l'oreille les juge pareil.
        //
        // Un tempo qui bouge de rien fait bouger toute la grille, et l'oeil voit ce
        // flottement bien avant de voir l'erreur qu'il corrige.
        Bpm = Bpm is { } previous && MathF.Abs(bpm - previous) < TempoStep
            ? previous
            : bpm;
    }
}
