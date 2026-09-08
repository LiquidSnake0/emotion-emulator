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
    private readonly float[] _smooth = new float[4];
    /// <summary>
    /// Le tempo. Construit dans le constructeur, avec le taux d'echantillonnage reel.
    ///
    /// IL ETAIT CONSTRUIT ICI, AVEC LE TAUX PAR DEFAUT, ET LE BUG A TENU LONGTEMPS.
    ///
    /// Le constructeur recevait le taux et le transmettait a tous les etages — bandes,
    /// harmonie, voix, timbre — sauf a celui-la, qui gardait 48 kHz quoi qu'il arrive.
    /// Sur un signal a 44,1 kHz, chaque fenetre etait donc supposee durer 21,3 ms alors
    /// qu'elle en dure 23,2 : les periodes d'autocorrelation etaient lues 8,8 % trop
    /// courtes, et le tempo d'autant trop rapide.
    ///
    /// Le meme extrait donnait <b>87,2 BPM a 48 kHz et 92,0 a 44,1</b>. Un tempo qui
    /// depend du taux d'echantillonnage ne mesure pas le morceau, il mesure le fichier.
    /// Et le cas n'a rien de theorique : un vinyle numerise, un logiciel de mix, une carte
    /// son grand public sortent tous du 44,1.
    /// </summary>
    private readonly TempoTracker _tempo;

    // L'harmonie travaille sur une fenetre quatre fois plus longue, pour separer les
    // demi-tons. Elle recoit les memes echantillons et se cadence toute seule.
    private readonly HarmonicAnalyzer _harmony;

    // Ce que les autres ne voient pas : tout ce qui change la couleur du son sans etre
    // une attaque ni un changement d'accord.
    private readonly NoveltyDetector _novelty = new();

    // Les instruments qui ne frappent pas. Ils travaillent sur la moitie harmonique de
    // la separation, qui etait jusqu'ici calculee puis jetee.
    private readonly VoiceTracker _voices;

    /// <summary>Le pipeline des voies, pour que la sonde rapporte ce qu'il a mesure.</summary>
    public SourcePipeline SourcePipeline => _voices.Pipeline;

    /// <summary>Les voies, pour lire leur maturite et leur poser un nom.</summary>
    public VoiceTracker Voix => _voices;

    /// <summary>La derniere image publiee, pour la sonde. Ce que le renderer a recu.</summary>
    public VisualFrame Derniere { get; private set; }

    /// <summary>Ou passe le temps, etage par etage. Eteint par defaut, et alors gratuit.</summary>
    public Etapes Etapes { get; } = new();

    /// <summary>
    /// Employer le domaine complexe plutot que le flux d'energie pour juger une attaque.
    /// Le temps de comparer les deux sur le meme signal.
    /// </summary>
    public bool FluxComplexeActif { get; set; }

    /// <summary>
    /// Part du domaine complexe dans le jugement du kick. Zero le desactive, et il ne coute
    /// alors rien.
    ///
    /// NUL PAR DEFAUT, ET C'EST LA MESURE QUI L'A DECIDE.
    ///
    /// L'idee etait bonne et le mecanisme fonctionne : une note qui commence repart d'une
    /// phase arbitraire, ce qu'un flux d'energie ne voit pas, et c'est exactement ce qui
    /// manque sur une frappe etouffee sous un sample sature.
    ///
    /// Le resultat est reel mais inegal. A poids 0,2, sur quatre morceaux : un enregistrement
    /// de set passe de 14 a 26 % de frappes bien calees et son verrouillage de 58 a 75 % ; un
    /// extrait propre passe de 69 a 80 % de verrouillage. Mais <b>Macroblank, le repertoire de
    /// reference, se degrade</b> — verrouillage de 62 a 49 %, frappes calees de 23 a 18.
    ///
    /// Et le reglage n'est pas stable : entre 0,2 et 0,3, le verrouillage moyen tombe de 64 a
    /// 45 %. Un optimum aussi etroit se regle sur du bruit, pas sur une propriete du signal.
    ///
    /// On garde donc le mecanisme et on le laisse eteint : il est disponible pour une matiere
    /// ou il aide, et il ne degrade rien tant que personne ne l'allume. Le figer a une valeur
    /// de compromis aurait empire le seul disque qu'on connaisse bien.
    /// </summary>
    public float PoidsComplexe { get; set; }

    private readonly ComplexFlux _fluxComplexe = new(Window / 2);

    /// <summary>Les deux fonctions de detection, pour les comparer sur la meme image.</summary>
    public float DernierFluxEnergie { get; private set; }
    public float DernierFluxComplexe { get; private set; }

    /// <summary>
    /// Ce que la fiche du cue affirmait du tempo, confronte a ce que le disque fait.
    ///
    /// Poser <c>Reference.Expected</c> depuis la fiche evite de recalculer ce qui a deja
    /// ete etabli au casque — et fait apparaitre la derive si le plateau s'ecarte.
    /// </summary>
    public TempoReference Reference { get; } = new();

    /// <summary>
    /// Reprend ce qu'on savait deja de ce disque : les portraits des sources et le tempo.
    ///
    /// Ce qui suit corrigera ces valeurs au lieu de les remplacer. C'est ce qui fait
    /// qu'arreter un morceau et le relancer ne perd rien, et que les seize temps du cue
    /// servent encore une fois le disque passe au master.
    /// </summary>
    /// <summary>
    /// Part du disque entrant deja passee au master, 0 a 1. Au-dela de zero et en deca de
    /// un, les deux disques sonnent ensemble : les voies suivent mais n'apprennent plus.
    /// </summary>
    public float Fondu
    {
        set => _voices.Apprend = value <= 0.02f || value >= 0.98f;
    }

    public void Reprendre(in TrackKnowledge connaissance)
    {
        _voices.Reprendre(connaissance);
        if (connaissance.Bpm > 0f) Reference.Expected = connaissance.Bpm;
    }

    /// <summary>Ce qu'on sait a cet instant, pret a etre range pour la prochaine ecoute.</summary>
    public TrackKnowledge Connaissance(string id, in TrackKnowledge precedente) =>
        new(id,
            _voices.Portraits(),
            _tempo.Bpm ?? precedente.Bpm,
            precedente.BpmObservations + 1,
            precedente.SecondsHeard + _secondesEcoutees);

    private float _secondesEcoutees;

    // LA SEPARATION PAR LE TIMBRE, ET NON PAR LA FREQUENCE.
    //
    // Un piano et un saxophone qui jouent la meme octave tombent dans la meme bande :
    // aucune finesse de decoupage ne les separe, on additionne leurs niveaux et l'on
    // obtient une grandeur qui ne decrit ni l'un ni l'autre. Ils ne partagent pas leur
    // timbre, et c'est par la qu'on les prend.
    private readonly SourceSeparator _separation;

    // La couleur du son. Elle travaille sur le spectre complet et non sur le percussif :
    // un filtre passe-bas agit sur tout, et le mesurer apres separation reviendrait a
    // regarder par le trou de la serrure.
    private readonly TimbreTracker _timbre;

    // Separation harmonique / percussive. Les bandes et les attaques travaillent alors
    // sur le percussif seul : le piano ne remplit plus les mediums de flux, et un clap
    // redevient detectable pour ce qu'il est.
    //
    // COUPEE PAR DEFAUT DEPUIS QU'ON L'A MESUREE SUR PLUSIEURS SOURCES.
    //
    // Elle a ete introduite pour empecher le piano de declencher les claps, et elle le
    // fait — le nombre de claps monte de 91 a 113 quand on la coupe. Mais elle coute
    // beaucoup plus qu'elle ne rapporte des qu'on regarde ailleurs.
    //
    // Verrouillage du temps fort, avec puis sans, sur quatre passages : 20 puis 86 % sur
    // un Macroblank, 84 puis 49 sur un passage du set, 52 puis 60, 35 puis 43. Moyenne
    // 48 contre 60, et surtout <b>pire cas 20 contre 43</b> — c'est le pire cas qui
    // decide en direct, un passage ou le systeme ignore le temps fort quatre fois sur
    // cinq se voyant bien davantage qu'une moyenne.
    //
    // Et elle coute la moitie de la latence d'analyse : <b>43 ms avec, 21 sans</b>.
    //
    // La raison de fond tient au repertoire. Le barber beats etouffe et filtre ses kicks
    // jusqu'a les noyer ; ce que la separation retient comme percussif y est surtout du
    // crepitement de bande, qui n'a ni periode ni accent. Elle retire du grave la basse,
    // laquelle porte la pulsation autant que la frappe.
    //
    // Reste activable par `Signal__Separate=true`, et le restera : sur un repertoire aux
    // kicks francs et au piano bavard, le calcul pourrait s'inverser.
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

    // Deux jeux publies a tour de role : rien n'est alloue par image.
    private readonly float[][] _sepPool =
        [new float[SourceSeparator.Sources], new float[SourceSeparator.Sources]];
    private readonly LaneState[][] _lanePool =
        [new LaneState[SourceSeparator.Sources], new LaneState[SourceSeparator.Sources]];
    private readonly float[][] _hautPool =
        [new float[SourceSeparator.Sources], new float[SourceSeparator.Sources]];
    private int _sepTurn;

    // Ou tombe l'attaque a l'interieur de la fenetre. Sans lui, un kick au premier
    // echantillon et un kick au dernier sont annonces au meme instant, a 21 ms pres.
    private readonly TransientLocator _transient = new();

    // Les gestes ramenes a des etats stables. Ce qui flotte n'est pas la valeur d'un
    // filtre mais la reponse a « est-il ferme », et c'est elle qui fait clignoter un motif.
    private readonly GestureTracker _gestures = new();

    // L'AMORTISSEMENT DESCEND DANS L'ANALYSE.
    //
    // Il vivait dans le renderer, ou chaque grandeur continue traversait un ressort avant
    // d'etre dessinee. L'unite CUDA aurait du les reimplementer tous, avec les memes
    // raideurs — et une regle qui vit en deux endroits finit par vivre de deux facons,
    // comme le Camelot avant elle.
    //
    // Les raideurs sont celles qu'employait le renderer, pour que le mouvement ne change
    // pas en changeant de place. Aucune latence n'est ajoutee : le ressort existait deja.
    private readonly Damper _dLevel = new(16f);
    private readonly Damper[] _dBands =
    [
        new(20f), new(20f), new(20f), new(20f), new(20f), new(20f),
        new(20f), new(20f), new(20f), new(20f), new(20f), new(20f),
    ];
    private readonly Damper _dLow = new(14f);    // la basse est lourde, elle traine
    private readonly Damper _dMid = new(22f);
    private readonly Damper _dHigh = new(40f);   // le xylophone est vif

    /// <summary>
    /// Duree d'une fenetre, en secondes. Elle depend du taux d'echantillonnage et ne peut
    /// donc pas etre une constante : 21,3 ms a 48 kHz, 23,2 a 44,1. Une constante y
    /// faisait avancer tous les ressorts 8,8 % trop vite sur un signal a 44,1.
    /// </summary>
    private readonly float _frameSeconds;

    // Le seuil de connaissance : quand annoncer au telephone qu'on en sait assez sur le
    // disque en cours pour que le GPU puisse basculer dessus.
    private readonly KnowledgeGate _gate = new();

    // Les bandes suivent une echelle logarithmique : l'oreille entend le rapport entre
    // deux frequences, pas leur difference. Douze bandes lineaires donneraient onze
    // bandes d'aigus et une seule pour tout le grave.
    private readonly int[] _edges;

    /// <param name="separate">
    /// Separer le percussif de l'harmonique avant analyse. Coute la latence annoncee par
    /// <see cref="Hpss.LatencyFrames"/>, soit 64 ms sur le reglage par defaut.
    /// </param>
    public SpectrumAnalyzer(int sampleRate = 48_000, bool separate = false)
    {
        _sampleRate = sampleRate;
        _frameSeconds = Window / (float)sampleRate;
        _tempo = new TempoTracker(sampleRate, Window);
        _edges = BuildEdges(sampleRate);
        _harmony = new HarmonicAnalyzer(sampleRate);
        _voices = new VoiceTracker(sampleRate, Window);
        _separation = new SourceSeparator(Window / 2);
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

    /// <summary>Les sources separees par le timbre, du grave a l'aigu.</summary>
    public SourceSeparator Separation => _separation;

    /// <summary>La courbe d'autocorrelation du tempo, pour le reglage.</summary>
    public IEnumerable<(float Bpm, float Raw, float Score)> TempoPeaks(int take = 6) =>
        _tempo.Peaks(take);

    /// <summary>Correlation brute a un tempo donne. Pour verifier une hypothese connue.</summary>
    public float TempoRawAt(float bpm) => _tempo.RawAt(bpm);

    /// <summary>Ce que le systeme sait du disque en cours, et s'il en sait assez.</summary>
    public Readiness Readiness => _gate.Current;

    /// <summary>Un nouveau disque commence : tout est a reapprendre.</summary>
    public void NewTrack() => _gate.Reset();

    /// <summary>Derniere rupture de continuite constatee, pour le journal.</summary>
    public string LastBreak => _continuity.Reason;

    /// <summary>
    /// Ecart de la derniere frappe a la grille, en fraction de temps. C'est la seule
    /// grandeur qui mesure la <b>justesse de la phase</b> — le verrouillage et l'ecart
    /// entre frappes, eux, sont comptes a la fenetre et ne peuvent pas la voir.
    /// </summary>
    public float SyncError => _grid.LastSyncError;

    /// <summary>Periode de la grille, en millisecondes. A comparer au tempo publie.</summary>
    public float GridBeatMs => _grid.BeatMs;

    /// <summary>Position de l'attaque dans la fenetre courante, en millisecondes.</summary>
    public float TransientOffsetMs => _transient.OffsetMs;

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

        Etapes.Debut();
        var harmony = _harmony.Feed(samples);
        Etapes.Fin(0);

        _transient.Feed(samples, _sampleRate);
        Etapes.Fin(1);

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
        // Le domaine complexe se calcule ici, tant que la transformee est encore intacte :
        // il lui faut la phase, que le module a jetee. Rien n'est calcule quand son poids
        // est nul — il coute une racine, un arc-tangente et un cosinus par bin.
        if (PoidsComplexe > 0f) _fluxComplexe.Feed(_re, _im, Math.Min(half, _edges[3]));

        var voices = Voices.None;
        Span<float> full = stackalloc float[half];
        spectrum.CopyTo(full);
        Etapes.Fin(2);                       // fenetrage, FFT et module

        if (_hpss is not null && _hpss.Feed(spectrum))
        {
            Etapes.Fin(3);                   // separation harmonique / percussive
            voices = _voices.Feed(_hpss.Harmonic);
            Etapes.Fin(4);                   // registres
            _hpss.Percussive.CopyTo(spectrum);
        }
        else
        {
            // LES REGISTRES TONALS NE DEPENDENT PAS DE LA SEPARATION.
            //
            // Ils n'etaient calcules que dans sa branche, si bien que la couper les a
            // supprimes d'un coup — et avec eux le disque des graves, le polygone de la
            // voix et les triangles des aigues, soit les deux tiers de ce qui se voit.
            // Le DJ l'a dit en trois mots : « il manque plein de sons ».
            //
            // Sans separation, on leur donne le spectre entier. C'est moins net qu'une
            // moitie harmonique — un coup de caisse claire fera bouger les trois
            // registres a la fois — mais infiniment preferable au silence.
            Etapes.Fin(3);                   // separation coupee : rien a compter
            voices = _voices.Feed(full);
            Etapes.Fin(4);
        }

        // Les six niveaux publies deviennent les six sources separees : ils decrivent des
        // timbres et non des tranches de frequence. Les agregats grave/medium/aigu
        // continuent de venir des registres, qui les rendent mieux.
        if (_separation.Pret)
        {
            var act = _sepPool[_sepTurn];
            var haut = _hautPool[_sepTurn];
            _sepTurn ^= 1;

            var max = 1e-4f;
            for (var i = 0; i < SourceSeparator.Sources; i++)
                max = MathF.Max(max, _separation.ActivationOrdonnee(i));

            for (var i = 0; i < SourceSeparator.Sources; i++)
            {
                act[i] = Clamp01(_separation.ActivationOrdonnee(i) / max);
                haut[i] = _separation.HauteurOrdonnee(i);
            }

            // CE QUI EST MESURE DOIT DECRIRE CE QUI EST AFFICHE.
            //
            // Les niveaux et les contours publies viennent de la separation par timbre ; la
            // nettete doit donc en venir aussi. Elle etait prise sur les bandes d'octave,
            // c'est-a-dire sur autre chose que ce que l'ecran montrait — et comme une bande
            // d'octave est presque toujours partagee, la jauge restait basse en decrivant un
            // objet que personne ne regardait.
            var etats = _lanePool[_sepTurn];
            for (var i = 0; i < SourceSeparator.Sources; i++)
            {
                var brut = _voices.EtatDe(i);
                etats[i] = brut with
                {
                    Level = act[i],
                    Position = haut[i],
                    Heard = _separation.EcouteOrdonnee(i),
                    Sharpness = _separation.StabiliteOrdonnee(i),
                };
            }

            voices = voices with { Levels = act, Pitches = haut, Lanes = etats };
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

        var fluxC = _fluxComplexe.DernierTotal;

        DernierFluxEnergie = flux;
        DernierFluxComplexe = fluxC;

        var onset = _onsets.Feed(FluxComplexeActif ? fluxC : flux);
        Etapes.Fin(6);                       // flux spectral et detection d'attaque
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
            // morceau soit pousse ou feutre, sans que le DJ ait a toucher a un niveau.
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
        // LE KICK SE JUGE SUR DEUX SIGNAUX, PAS UN.
        //
        // La montee des bandes voit ce qui monte en energie ; elle est aveugle a une frappe
        // etouffee sous un sample sature, ce qui est la moitie du repertoire. Le domaine
        // complexe, lui, voit qu'une note repart d'une phase arbitraire meme quand son
        // amplitude bouge peu. Les deux sont ramenes a un rapport sans dimension avant
        // d'etre melanges, sinon le poids du melange dependrait du volume du disque.
        var rKick = Smooth(0, BandRise(bands, 0, 3) + PoidsComplexe * _fluxComplexe.Rapport);
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

        // LE TEMPO SE MESURE AVANT LA SEPARATION, SUR LE SPECTRE ENTIER.
        //
        // C'est le meme raisonnement que pour le timbre, et il a fallu une verite terrain
        // pour le voir. Sur un morceau de Macroblank donne a 90 BPM, la separation
        // faisait disparaitre la pulsation : correlation 0,066 a 90 BPM une fois separe,
        // contre 0,226 a 85 BPM sur le signal complet. Le pic dominant tombait a 108, un
        // tempo qui n'existe pas dans ce morceau.
        //
        // La raison tient au repertoire. Le barber beats etouffe et filtre ses kicks
        // jusqu'a les noyer ; ce que HPSS retient comme « percussif » y est alors surtout
        // du crepitement de bande, qui n'a aucune periode. La pulsation, elle, est portee
        // par le morceau <b>entier</b> — par la basse, par les accords qui pulsent, par
        // tout ce que la separation met de cote.
        //
        // La separation reste indispensable ailleurs : sans elle, le piano declenche les
        // claps. Elle sert donc a decider <i>ce qui frappe</i>, jamais a decider <i>a
        // quelle vitesse ca tourne</i>.
        var pulse = 0f;
        for (var i = 1; i < kickBins; i++) pulse += full[i];
        Etapes.Fin(7);                       // les douze bandes
        _tempo.Feed(Smooth(3, PulseRise(pulse)));
        Etapes.Fin(8);                       // autocorrelation du tempo

        // L'ECART MINIMAL SUIT LE TEMPO, IL N'EST PLUS UNE CONSTANTE.
        //
        // Quatre-vingt-cinq centiemes de temps : assez pour refuser tout ce qui tombe entre
        // deux temps — les 0,75 temps qui dominaient les mesures — sans refuser un temps
        // dont la frappe arrive un peu tot. Un kick legerement en avance reste un kick ;
        // une frappe aux trois quarts du temps n'en est pas un.
        _kick.Suivre(_tempo.Bpm, _frameSeconds * 1000f, 0.85f);

        var kick = _kick.Feed(rKick);
        var clap = _clap.Feed(rClap);

        // Un clap ne compte que si le medium l'emporte franchement sur le grave a cet
        // instant. Sinon c'est le corps du kick qu'on entend monter dans le medium, et
        // l'eclair partirait sur le kick — ce qui est exactement ce qu'il ne faut pas.
        if (clap && rClap < rKick * 1.3f) clap = false;

        var hits = new Hits(kick, clap, _hat.Feed(rHat));
        Etapes.Fin(9);                       // kick, clap, charley

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
        // La separation travaille sur le spectre entier : c'est le timbre complet qui
        // distingue deux instruments, pas sa moitie percussive.
        // Le suivi seul : l'apprentissage des profils tourne dans un fil de fond et ne
        // compte pas dans le temps d'une image. C'est tout l'interet de l'avoir sorti.
        _separation.Feed(full);
        Etapes.Fin(5);                       // suivi des timbres separes

        var timbre = _timbre.Feed(full, eventCount);
        Etapes.Fin(10);                      // couleur du son

        // Les grandeurs continues partent amorties ; les evenements restent bruts. Une
        // impulsion lissee n'est plus une impulsion, et c'est le renderer qui les
        // declenche — la seule chose qu'il calcule encore.
        var dt = _frameSeconds;
        level = _dLevel.Feed(level, dt);
        for (var i = 0; i < bands.Length; i++) bands[i] = _dBands[i].Feed(bands[i], dt);
        voices = voices with
        {
            Low = _dLow.Feed(voices.Low, dt),
            Mid = _dMid.Feed(voices.Mid, dt),
            High = _dHigh.Feed(voices.High, dt),
        };

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

        // La grille se cale sur l'instant reel de la frappe, pas sur celui de la fenetre
        // qui la contient. Vingt et une millisecondes d'incertitude en moins, sur le seul
        // etage ou la justesse de la phase decide de tout.
        var at = tMs + (long)_transient.OffsetMs;
        if (hits.Kick) { _grid.Sync(at); _grid.MarkKick(at); }
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
            if (_continuity.WasSilence) { _tempo.Reset(); _gate.Reset(); }
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

        var gestures = _gestures.Feed(timbre.Openness, bass, timbre.Density);
        var readiness = _gate.Feed(tMs, _tempo.Bpm, timbre.Centroid, bass, timbre.Density);

        _secondesEcoutees = tMs / 1000f;

        // La fiche du cue, confrontee au disque. Sans fiche, la derive reste nulle et rien
        // ne change : on ne fabrique pas d'ecart avec une reference qu'on n'a pas.
        Reference.Feed(_tempo.Bpm, tMs);

        Etapes.Fin(11);                      // structure, gestes, nouveaute
        Etapes.Image();

        return Derniere = new VisualFrame(
            tMs, level, bands, onset, _tempo.Phase(tMs), _tempo.Bpm,
            Hits: hits,
            Harmony: harmony,
            Voices: voices,
            Timbre: timbre,
            Structure: structure,
            Gestures: gestures,
            Readiness: readiness,
            Novelty: _novelty.Level,
            NoveltyOnset: _novelty.Onset,
            Flux: Clamp01(rKick / scale),
            Threshold: Clamp01(_kick.Threshold / scale),
            ExpectedBpm: Reference.Expected,
            TempoDrift: Reference.Drift,
            DriftVisible: Reference.Visible,
            AnnouncedBpm: Reference.Announcement,
            TempoAnnounce: Reference.Announced);
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
    /// <summary>
    /// Montee de l'energie grave du spectre complet, en rapport contre un masque — meme
    /// mecanique que pour les registres, et pour la meme raison : un rapport est invariant
    /// au niveau, un masque se laisse depasser par ce qui est plus fort.
    /// </summary>
    private float PulseRise(float energy)
    {
        var ratio = energy / (_pulseMask + 1f);
        if (_pulseAge >= MaskHold) _pulseMask *= MaskDecay;
        if (energy > _pulseMask) { _pulseMask = energy; _pulseAge = 0; }
        _pulseAge++;

        return ratio > 1f ? ratio - 1f : 0f;
    }

    private float _pulseMask;
    private int _pulseAge;

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
