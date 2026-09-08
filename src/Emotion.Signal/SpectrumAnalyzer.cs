namespace Emotion.Signal;

/// <summary>
/// Transforme une fenetre d'echantillons en une image du signal : niveau, bandes,
/// attaque, tempo.
///
/// Sans etat externe, entierement testable : on lui donne des echantillons, elle rend
/// une <see cref="VisualFrame"/>. Les sources se contentent de la nourrir, qu'elles
/// viennent d'une carte son ou d'un fichier.
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>Taille de fenetre. 1024 a 48 kHz, soit 21 ms : assez court pour qu'un kick reste net.</summary>
    public const int Window = 1024;

    private readonly int _sampleRate;
    private readonly float[] _hann = Fft.Hann(Window);
    private readonly float[] _re = new float[Window];
    private readonly float[] _im = new float[Window];

    private readonly float[] _prevSpectrum = new float[Window / 2];

    // Maximum glissant par bande, pour normaliser sur ce qui joue plutot que sur une
    // constante. Sans lui, un morceau fort sature les douze bandes a 1 et le visuel
    // n'a plus aucun relief, tandis qu'un morceau feutre ne fait rien bouger.
    private readonly float[] _bandPeak = new float[VisualFrame.BandCount];
    private readonly OnsetDetector _onsets = new();

    // Un detecteur par registre. Ils partagent la mecanique — flux positif, seuil
    // adaptatif, ecart minimal — mais chacun ne regarde que sa tranche de spectre,
    // et chacun se cale donc sur le niveau de bruit qui lui est propre.
    private readonly OnsetDetector _kick = new();
    private readonly OnsetDetector _clap = new();
    private readonly OnsetDetector _hat = new(minGap: 4);   // les charleys vont vite
    private readonly float[] _prevBand = new float[VisualFrame.BandCount];

    // Deux jeux de bandes, utilises a tour de role. Un tableau neuf a chaque image
    // faisait quarante-sept allocations par seconde, donc des collectes regulieres —
    // et une collecte tombe forcement, un jour, pendant l'ecriture vers l'anneau. Deux
    // suffisent : le consommateur en ligne a fini d'en lire un avant que le suivant ne
    // soit reecrit, et les files bornees n'en gardent au plus que deux.
    private readonly float[][] _bandPool =
    [
        new float[VisualFrame.BandCount],
        new float[VisualFrame.BandCount],
    ];
    private int _bandTurn;

    // Enveloppes lissees des trois registres. Le flux brut est en dents de scie d'une
    // fenetre a l'autre : y chercher un maximum local revient a compter le bruit. Une
    // moyenne mobile courte en fait une enveloppe ou un sommet veut dire quelque chose.
    private readonly float[] _smooth = new float[3];
    private readonly TempoTracker _tempo = new();

    // L'harmonie travaille sur une fenetre quatre fois plus longue, pour separer les
    // demi-tons. Elle recoit les memes echantillons et se cadence toute seule.
    private readonly HarmonicAnalyzer _harmony;

    // Ce que les autres ne voient pas : tout ce qui change la couleur du son sans etre
    // une attaque ni un changement d'accord.
    private readonly NoveltyDetector _novelty = new();

    // Les instruments qui ne frappent pas. Ils travaillent sur la moitie harmonique de
    // la separation, qui etait jusqu'ici calculee puis jetee.
    private readonly VoiceTracker _voices;

    // La couleur du son. Elle travaille sur le spectre complet et non sur le percussif :
    // un filtre passe-bas agit sur tout, et le mesurer apres separation reviendrait a
    // regarder par le trou de la serrure.
    private readonly TimbreTracker _timbre;

    // Separation harmonique / percussive, appliquee avant tout le reste. Les bandes et
    // les attaques travaillent alors sur le percussif seul : le piano ne remplit plus
    // les mediums de flux, et un clap redevient detectable pour ce qu'il est.
    //
    // Optionnelle : elle coute 64 ms de latence, et on doit pouvoir comparer avec et
    // sans sur le meme morceau pour juger si le gain les vaut.
    private readonly Hpss? _hpss;

    // La grille metrique et la tension. Elles ne regardent aucun echantillon : elles ne
    // consomment que ce que les autres ont deja conclu. C'est le premier etage du projet
    // qui travaille sur le temps long — huit mesures — la ou tout le reste vit dans
    // l'instant.
    private readonly BeatGrid _grid = new();
    private readonly ArcDetector _arc = new();

    // La structure longue. Elle autocorrele une signature de mesure la ou TempoTracker
    // autocorrele une enveloppe d'attaque : meme idee, un ordre de grandeur au-dessus.
    private readonly SectionTracker _section = new();

    // Le disque joue-t-il toujours ce qu'on croit qu'il joue. Sans elle, un saut de sillon
    // laisse la grille decrire pendant plus d'une minute un endroit du disque ou l'on
    // n'est plus, son oubli etant lent par construction.
    private readonly ContinuityWatch _continuity = new();

    // Le temps fort cherche dans ce que portent les quatre temps, et non dans ce qui s'y
    // declenche. Un kick present sur les quatre temps n'apprend rien ; le fait que celui
    // du premier soit plus appuye, si.
    private readonly DownbeatProfile _profile = new();

    // Les bandes suivent une echelle logarithmique : l'oreille entend le rapport entre
    // deux frequences, pas leur difference. Douze bandes lineaires donneraient onze
    // bandes d'aigus et une seule pour tout le grave.
    private readonly int[] _edges;

    /// <param name="separate">
    /// Separer le percussif de l'harmonique avant analyse. Coute la latence annoncee par
    /// <see cref="Hpss.LatencyFrames"/>, soit 64 ms sur le reglage par defaut.
    /// </param>
    public SpectrumAnalyzer(int sampleRate = 48_000, bool separate = true)
    {
        _sampleRate = sampleRate;
        _edges = BuildEdges(sampleRate);
        _harmony = new HarmonicAnalyzer(sampleRate);
        _voices = new VoiceTracker(sampleRate, Window);
        _timbre = new TimbreTracker(sampleRate, Window);
        // Trois fenetres et non sept : le retard tombe de 64 a 21 ms. La separation est
        // un peu moins nette, mais elle reste tres suffisante pour empecher le piano de
        // declencher les claps — et surtout elle cesse de desynchroniser le visuel.
        _hpss = separate ? new Hpss(Window / 2, timeFrames: 3, freqBins: 17) : null;
    }

    /// <summary>La separation est-elle active.</summary>
    public bool Separating => _hpss is not null;

    /// <summary>Ce que le profil des quatre temps designe, pour le reglage.</summary>
    public (int Offset, float Confidence, IReadOnlyList<float> Scores, int GridBeat) Downbeat =>
        (_profile.Offset, _profile.Confidence, _profile.Scores, _grid.Beat);

    /// <summary>Derniere rupture de continuite constatee, pour le journal.</summary>
    public string LastBreak => _continuity.Reason;

    /// <summary>Une rupture vient d'etre constatee sur cette fenetre.</summary>
    public bool ContinuityBroken => _continuity.Broken;

    /// <summary>L'etat du suivi de structure longue, pour le reglage.</summary>
    public (int Bars, float Best, IReadOnlyList<float> Scores) Section =>
        (_section.PhraseBars, _section.BestScore, _section.Scores);

    /// <summary>Les pentes de la tension, pour le reglage et la sonde hors ligne.</summary>
    public (float Bright, float Bass, float Busy) Slopes =>
        (_arc.SlopeBright, _arc.SlopeBass, _arc.SlopeBusy);

    /// <summary>
    /// Retard total entre le son et la detection, en millisecondes. La somme des deux
    /// etages : separation puis recherche de sommet.
    ///
    /// Il doit rester sous quarante millisecondes, seuil au-dela duquel l'oeil cesse de
    /// lier une image au son qui l'a declenchee. C'est une grandeur qu'on affiche, pas
    /// qu'on subit.
    /// </summary>
    public float LatencyMs =>
        ((_hpss?.LatencyFrames ?? 0) + OnsetDetector.Lookahead) * 1000f / _sampleRate * Window;

    /// <summary>Tempo estime, nul tant que la detection n'a pas accroche.</summary>
    public float? Bpm => _tempo.Bpm;

    /// <summary>
    /// Reprend le tempo d'un autre analyseur comme point de depart. Voir
    /// <see cref="TempoTracker.Adopt"/> : c'est une amorce, pas un verrou, et
    /// l'analyse du master continue de chercher a partir de la.
    /// </summary>
    public void AdoptTempo(float bpm, long tMs) => _tempo.Adopt(bpm, tMs);

    /// <summary>
    /// Analyse une fenetre. <paramref name="samples"/> doit contenir
    /// <see cref="Window"/> echantillons mono dans [-1, 1].
    /// </summary>
    public VisualFrame Analyze(ReadOnlySpan<float> samples, long tMs)
    {
        if (samples.Length != Window)
            throw new ArgumentException($"fenetre de {Window} echantillons attendue", nameof(samples));

        var harmony = _harmony.Feed(samples);

        var sum = 0f;
        for (var i = 0; i < Window; i++)
        {
            var s = samples[i];
            sum += s * s;
            _re[i] = s * _hann[i];
            _im[i] = 0f;
        }

        // Racine de la moyenne des carres, puis compression : un signal musical vit
        // dans le bas de l'echelle lineaire, et un visuel qui suit le RMS brut reste
        // ecrase en permanence.
        var rms = MathF.Sqrt(sum / Window);
        var level = Clamp01(MathF.Pow(rms * 3.2f, 0.55f));

        Fft.Forward(_re, _im);

        var half = Window / 2;
        Span<float> spectrum = stackalloc float[half];
        for (var i = 0; i < half; i++)
            spectrum[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

        // La separation remplace le spectre par sa seule composante percussive. Tant
        // que son tampon n'est pas plein elle ne rend rien, et on travaille alors sur
        // le spectre complet : mieux vaut une analyse imparfaite qu'un ecran noir
        // pendant les premieres fenetres.
        // Le timbre se mesure avant la separation, sur le spectre entier.
        // La separation rend les deux composantes. On garde la percussive pour les
        // bandes et les attaques, et on donne l'harmonique aux registres tonals : sans
        // elle, chaque coup de caisse claire ferait bondir les trois a la fois.
        var voices = Voices.None;
        Span<float> full = stackalloc float[half];
        spectrum.CopyTo(full);

        if (_hpss is not null && _hpss.Feed(spectrum))
        {
            voices = _voices.Feed(_hpss.Harmonic);
            _hpss.Percussive.CopyTo(spectrum);
        }

        // Flux spectral positif : on ne compte que ce qui monte. Une note qui s'eteint
        // n'est pas une attaque.
        //
        // Il est calcule sur le seul registre du kick, pas sur tout le spectre, et
        // c'est un choix dicte par le repertoire. Le barber beats est plein de souffle,
        // de crepitement de vinyle et de nappes qui bougent : ce bruit remplit les
        // aigus de flux en permanence et noie la seule montee qui compte. Mesure sur
        // instamata : en pleine bande, le detecteur voyait quatre attaques par temps.
        var kickBins = Math.Min(half, _edges[KickBandLimit]);

        var flux = 0f;
        for (var i = 0; i < half; i++)
        {
            var d = spectrum[i] - _prevSpectrum[i];
            if (d > 0 && i < kickBins) flux += d;
            _prevSpectrum[i] = spectrum[i];
        }

        var onset = _onsets.Feed(flux);
        if (onset) _tempo.Mark(tMs);

        var bands = _bandPool[_bandTurn];
        _bandTurn ^= 1;
        for (var b = 0; b < bands.Length; b++)
        {
            var lo = _edges[b];
            var hi = _edges[b + 1];
            var peak = 0f;
            for (var i = lo; i < hi && i < half; i++)
                if (spectrum[i] > peak) peak = spectrum[i];

            // Le pic plutot que la moyenne : sur une bande large, une moyenne noie une
            // pointe unique, or c'est justement la pointe qui se voit a l'ecran.
            //
            // Puis normalisation sur le maximum recent de cette bande, qui redescend
            // lentement. C'est un controle de gain : le visuel garde son relief que le
            // morceau soit pousse ou feutre, sans que Selim ait a toucher a un niveau.
            _bandPeak[b] = MathF.Max(peak, _bandPeak[b] * PeakDecay);
            var reference = MathF.Max(_bandPeak[b], MinReference);
            bands[b] = Clamp01(MathF.Pow(peak / reference, 0.7f));
        }

        // Flux par registre, calcule sur les bandes deja normalisees : chaque detecteur
        // se cale ainsi sur le contraste de sa tranche et non sur son volume absolu,
        // ce qui evite qu'un mix charge en graves eteigne la detection des claps.
        //
        // Les tranches ne se chevauchent plus : la bande 3 appartenait aux deux, et un
        // kick y bavait assez pour declencher le detecteur de clap. Mesure a l'ecran de
        // diagnostic : 80 kicks et 81 claps, tombant aux memes instants.
        var rKick = Smooth(0, BandRise(bands, 0, 3));
        var rClap = Smooth(1, BandRise(bands, 4, 9));
        // LES CHARLEYS GARDENT LA DIFFERENCE, ET CE N'EST PAS UNE EXCEPTION DE CONFORT.
        //
        // Un rapport n'est informatif que dans un registre qui se vide entre deux frappes.
        // Or les aigus de ce repertoire ne se vident jamais : le souffle de bande et le
        // crepitement de vinyle y entretiennent un plancher permanent — c'est deja ce qui
        // avait force a limiter le flux au registre du kick. Le masque y reste donc haut,
        // le rapport ne depasse jamais un, et la mesure est sans appel : 23 charleys au
        // lieu de 302 sur cent secondes.
        //
        // Le grave et le medium, eux, se vident entre deux frappes. Ils prennent le
        // rapport, qui les rend invariants au niveau ; les aigus gardent la difference.
        var rHat  = Smooth(2, BandRise(bands, 9, VisualFrame.BandCount, ratioMode: false));

        // Le tempo se mesure sur l'enveloppe du kick, pas sur les frappes qu'on en tire :
        // l'autocorrelation n'a besoin d'aucune decision binaire, et se moque donc qu'une
        // frappe ait ete manquee ou inventee.
        _tempo.Feed(rKick);

        var kick = _kick.Feed(rKick);
        var clap = _clap.Feed(rClap);

        // Un clap ne compte que si le medium l'emporte franchement sur le grave a cet
        // instant. Sinon c'est le corps du kick qu'on entend monter dans le medium, et
        // l'eclair partirait sur le kick — ce qui est exactement ce qu'il ne faut pas.
        if (clap && rClap < rKick * 1.3f) clap = false;

        var hits = new Hits(kick, clap, _hat.Feed(rHat));

        UpdateMasks(bands);
        Array.Copy(bands, _prevBand, bands.Length);

        // Ce que rapporte le diagnostic est l'enveloppe du <b>kick</b> et son seuil, pas
        // le flux global : ce sont les enveloppes par registre qui decident des attaques,
        // et un ecran qui afficherait une autre grandeur ferait regler a cote. Un outil
        // de reglage qui montre autre chose que ce qui decide est pire qu'aucun outil.
        var scale = MathF.Max(_kick.Threshold * 2f, 1e-6f);

        _novelty.Feed(bands);

        // Densite : tout ce qui s'est declenche sur cette fenetre. Un passage calme
        // n'est pas seulement plus sourd, il est plus vide.
        var eventCount = (hits.Kick ? 1 : 0) + (hits.Clap ? 1 : 0) + (hits.Hat ? 1 : 0)
                       + (voices.LowHit ? 1 : 0) + (voices.MidHit ? 1 : 0)
                       + (voices.HighHit ? 1 : 0);
        var timbre = _timbre.Feed(full, eventCount);

        // Les graves sont pris sur les bandes normalisees et non sur le RMS : c'est leur
        // poids relatif qui compte, et un morceau joue fort ne doit pas paraitre plus
        // structure qu'un autre.
        var bass = (bands[0] + bands[1] + bands[2]) / 3f;

        // La grille metrique. Elle est nourrie de conclusions, jamais de signal : le kick
        // la recale, le kick, le clap et le changement d'accord votent pour le temps fort,
        // et une rupture de section realigne la phrase.
        // Chaque indice vote pour le temps le plus proche de l'instant ou il tombe, et
        // non pour « le temps en cours » : le kick arrive a quelques millisecondes de la
        // frontiere, et le moindre flottement de la grille le ferait changer de camp.
        _arc.Feed(bass, timbre.Centroid, timbre.Density);
        var stepped = _grid.Advance(tMs, _tempo.Bpm);
        if (stepped) _arc.Advance();

        if (hits.Kick) { _grid.Sync(tMs); _grid.MarkKick(tMs); }
        if (hits.Clap) _grid.MarkClap(tMs);
        if (harmony.Change > ChordChangeVote) _grid.MarkChange(tMs);
        if (_novelty.Onset) _grid.MarkSection(tMs);

        // Le profil se nourrit du continu : l'energie grave, la montee du registre du
        // kick, le mouvement harmonique. Aucune de ces trois n'est un evenement.
        _profile.Feed(_grid.BeatIndex, bass, rKick, harmony.Change);
        if (_grid.BarStart && _profile.Confidence > 0.2f)
            _grid.MarkProfile(_profile.Offset, 1.5f * _profile.Confidence);

        _continuity.Feed(level, hits.Kick, _grid.LastSyncError);
        if (_continuity.Broken)
        {
            // ON ATTENUE, ON N'EFFACE PAS.
            //
            // Un effacement complet part du principe que la detection de rupture ne se
            // trompe jamais. Elle se trompe : mesuree a l'origine, elle voyait dix
            // ruptures en cent secondes sur un set qui n'en contenait aucune, et chacune
            // remettait a zero un temps fort qui avait demande une minute d'ecoute.
            //
            // En attenuant fortement, une vraie rupture laisse la nouvelle information
            // l'emporter en quelques mesures, tandis qu'une fausse ne coute qu'un peu de
            // confiance passagere. On jette de meme ce qui decrit une position, jamais ce
            // qui decrit une vitesse : un saut de sillon laisse le meme disque au meme
            // tempo.
            _grid.Weaken(0.5f);
            _section.Reset();
            _arc.Reset();
            _profile.Reset();
            if (_continuity.WasSilence) _tempo.Reset();
        }

        // La signature de la mesure en cours, close a chaque debut de mesure.
        _section.Feed(bands, timbre.Centroid, timbre.Density);
        if (_grid.BarStart) _section.CloseBar();

        var beat = _grid.Beat;
        var inBar = beat < 0 ? 0f : (beat + _grid.Phase) / 4f;
        var bars = _section.PhraseBars;
        var bar = _section.BarInPhrase;
        var structure = new Structure(
            beat,
            bar,
            (bar + inBar) / bars,
            _grid.Confidence,
            _arc.Buildup,
            // La rupture ne vaut que pour la fenetre ou elle est constatee. L'arc n'avance
            // qu'une fois par temps, et republier son verdict a chaque fenetre ferait durer
            // un evenement instantane une trentaine d'images — la sonde en comptait vingt
            // au meme instant.
            stepped && _arc.Drop,
            _grid.BarStart,
            _grid.BarStart && bar == 0,
            bars,
            _section.BarsToBoundary,
            _section.Confidence,
            _continuity.Trust);

        return new VisualFrame(
            tMs, level, bands, onset, _tempo.Phase(tMs), _tempo.Bpm,
            Hits: hits,
            Harmony: harmony,
            Voices: voices,
            Timbre: timbre,
            Structure: structure,
            Novelty: _novelty.Level,
            NoveltyOnset: _novelty.Onset,
            Flux: Clamp01(rKick / scale),
            Threshold: Clamp01(_kick.Threshold / scale));
    }

    /// <summary>
    /// Bornes des bandes, de 30 Hz a 16 kHz, reparties geometriquement.
    /// </summary>
    private static int[] BuildEdges(int sampleRate)
    {
        const float lowHz = 30f, highHz = 16_000f;
        var n = VisualFrame.BandCount;
        var edges = new int[n + 1];
        var binHz = sampleRate / (float)Window;

        for (var i = 0; i <= n; i++)
        {
            var hz = lowHz * MathF.Pow(highHz / lowHz, i / (float)n);
            edges[i] = Math.Max(1, (int)(hz / binHz));
        }

        // Deux bornes egales donneraient une bande vide : on force la croissance.
        for (var i = 1; i <= n; i++)
            if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 1;

        return edges;
    }

    /// <summary>
    /// Moyenne mobile a deux termes sur l'enveloppe d'un registre. Deux et pas plus :
    /// au-dela, l'attaque s'etale et le sommet se deplace, donc l'effet visuel arrive
    /// en retard sur ce qu'on entend.
    /// </summary>
    private float Smooth(int slot, float v)
    {
        var s = (_smooth[slot] + v) * 0.5f;
        _smooth[slot] = v;
        return s;
    }

    /// <summary>Montee d'energie sur une tranche de bandes, depuis la fenetre precedente.</summary>
    /// <summary>
    /// Montee d'un registre, mesuree comme un <b>rapport</b> et non comme une difference.
    ///
    /// C'est le choix de <c>bonk~</c>, le detecteur d'attaques de Puckette, et il tient a
    /// une propriete simple : un rapport est invariant au niveau. Un kick doux dans un
    /// passage doux produit la meme montee qu'un kick fort dans un passage fort, tandis
    /// qu'une difference ne voit que le second.
    ///
    /// C'est exactement le probleme rencontre ici — un mix charge en graves qui eteignait
    /// la detection des claps — traite a sa source plutot que contourne par une
    /// normalisation.
    ///
    /// LE RAPPORT SE PREND CONTRE UN MASQUE, JAMAIS CONTRE LA FENETRE PRECEDENTE.
    ///
    /// Les deux emprunts a <c>bonk~</c> sont indissociables, et l'avoir ignore s'est vu
    /// tout de suite : rapporte a la fenetre precedente, qui peut etre quasi nulle, le
    /// rapport explose sur du bruit. Mesure : les charleys tombaient de 331 a 121 sur le
    /// meme passage.
    ///
    /// Le masque, lui, suit la crete de la bande — il monte instantanement avec le signal,
    /// se maintient quelques fenetres, puis decroit. C'est une reference stable, et un
    /// rapport pris contre lui veut dire quelque chose.
    ///
    /// Il remplace du meme coup l'ecart minimal, qui interdisait toute detection pendant
    /// vingt fenetres sans nuance : une frappe forte suivant de peu une frappe faible
    /// etait perdue. Le masque, lui, se laisse depasser par ce qui est plus fort. Il est
    /// musical la ou la regle etait administrative.
    /// </summary>
    private float BandRise(float[] bands, int from, int to, bool ratioMode = true)
    {
        var rise = 0f;
        for (var i = from; i < to && i < bands.Length; i++)
        {
            if (ratioMode)
            {
                var ratio = bands[i] / (_bandMask[i] + RiseFloor);
                if (ratio > 1f) rise += ratio - 1f;
            }
            else
            {
                var d = bands[i] - _prevBand[i];
                if (d > 0) rise += d;
            }
        }

        return rise;
    }

    /// <summary>
    /// Met a jour le masque de chaque bande : maintien puis decroissance, et remontee
    /// immediate des que le signal depasse.
    /// </summary>
    private void UpdateMasks(float[] bands)
    {
        for (var i = 0; i < bands.Length; i++)
        {
            if (_maskAge[i] >= MaskHold) _bandMask[i] *= MaskDecay;
            if (bands[i] > _bandMask[i]) { _bandMask[i] = bands[i]; _maskAge[i] = 0; }
            _maskAge[i]++;
        }
    }

    private readonly float[] _bandMask = new float[VisualFrame.BandCount];
    private readonly int[] _maskAge = new int[VisualFrame.BandCount];

    private const float RiseFloor = 0.05f;

    /// <summary>Fenetres de maintien du masque avant qu'il ne commence a decroitre.</summary>
    private const int MaskHold = 1;

    /// <summary>
    /// Decroissance du masque, par fenetre.
    ///
    /// J'ai d'abord voulu « adapter » le 0,7 de <c>bonk~</c> a nos fenetres huit fois plus
    /// longues, en le remontant a 0,94 pour garder la meme constante de temps. C'etait
    /// prendre le probleme a l'envers, et la mesure l'a dit aussitot : <b>5 charleys
    /// detectes sur cent secondes</b> au lieu de trois cents.
    ///
    /// La raison est que le masque n'est pas la pour lisser mais pour <b>s'effacer entre
    /// deux frappes</b>. S'il tient encore quand la suivante arrive, une frappe reguliere
    /// de meme amplitude ne produit aucun rapport et devient invisible. Il doit donc
    /// s'effondrer en moins d'une croche — soit precisement ce que fait le 0,7 d'origine.
    /// </summary>
    private const float MaskDecay = 0.7f;

    /// <summary>
    /// Jusqu'ou monte le registre pris en compte pour les attaques : les cinq premieres
    /// bandes, soit environ 30 a 400 Hz. Le kick et le bas de la caisse claire.
    /// </summary>
    // Au-dela de quoi un ecart de profil harmonique compte comme un changement d'accord
    // dans le vote du temps fort.
    //
    // MESURE, PAS DEVINE. A 0,45 — la valeur que la formule appelait « raisonnable » —
    // la sonde comptait 935 changements d'accord en deux minutes, soit huit par seconde :
    // ce n'etait plus un indice rare, c'etait un vote uniforme pour tous les temps, donc
    // aucune information. La distribution reelle sur un set enregistre donne p50 = 0,27
    // et p99 = 0,77. Le seuil doit vivre dans la queue de cette distribution, pas au
    // milieu — un indice qui se produit tout le temps ne discrimine rien.
    private const float ChordChangeVote = 0.75f;

    private const int KickBandLimit = 5;

    /// <summary>
    /// Vitesse a laquelle le maximum d'une bande redescend, par fenetre. A 0,999 sur
    /// 47 fenetres par seconde, il faut une quinzaine de secondes pour oublier un pic :
    /// assez lent pour ne pas pomper au rythme de la musique, assez rapide pour suivre
    /// un changement de morceau.
    /// </summary>
    private const float PeakDecay = 0.999f;

    /// <summary>Plancher, pour que le silence ne soit pas amplifie en bruit plein ecran.</summary>
    private const float MinReference = 4f;

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
