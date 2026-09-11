namespace Emotion.Signal;

/// <summary>
/// Separe le son en sources, sans savoir lesquelles.
///
/// LE PROBLEME QU'AUCUN FILTRE DE FREQUENCE NE RESOUT. Un piano et un saxophone qui jouent
/// dans la meme octave tombent dans la meme bande : quelle que soit la finesse du
/// decoupage, on additionne leurs deux niveaux et l'on obtient une grandeur qui ne decrit
/// ni l'un ni l'autre. Le DJ l'a dit ainsi : « piano et saxophone qui s'additionnent, ca
/// donne un truc illisible ».
///
/// Deux sons peuvent partager une hauteur ; ils ne partagent pas leur <b>timbre</b>. Et un
/// timbre, ce n'est pas un profil fige : c'est une forme qui <b>glisse avec la note</b>.
/// C'est ce que <see cref="ProfileLearner"/> apprend — un gabarit par source, sur un axe
/// logarithmique en frequence, libre de se placer sur deux octaves.
///
/// DEUX REGIMES, ET C'EST CE QUI LA REND UTILISABLE EN DIRECT.
///
///   apprendre    gabarits et niveaux ensemble, sur quarante secondes. Couteux, fait
///                rarement, et hors du fil d'analyse — c'est le travail du cue.
///   suivre       gabarits figes, niveaux et positions seuls, sur l'image courante.
///                Quelques centaines de milliers d'operations, dans le fil.
///
/// CE QU'ELLE VOIT. Pas le spectre d'analyse a 1024 : a 47 Hz la case, le grave n'a que
/// quatre cases et rien ne peut y glisser. Elle garde ses propres 4096 echantillons, prend
/// sa propre transformee a chaque image et la projette sur 192 cases logarithmiques, vingt-
/// quatre par octave. Le suivi du tempo, des frappes et des registres ne change pas.
/// </summary>
public sealed class SourceSeparator
{
    /// <summary>
    /// Capacite : combien de sources le paquet peut porter. Le nombre publie, lui, est
    /// decouvert par disque (<see cref="Actives"/>).
    /// </summary>
    public const int Sources = 6;

    /// <summary>
    /// Combien de temps la separation ecoute pour apprendre. QUARANTE SECONDES, ET C'EST
    /// LE CUE.
    ///
    /// Elle en regardait 2,7 : sur un album entier, cela donnait des tranches de registre et
    /// jamais des instruments. Le DJ a donne la duree qui compte : « un disque reste au
    /// casque dans les 40 secondes ». C'est le temps dont on dispose avant qu'il passe au
    /// master, et c'est donc la fenetre d'apprentissage.
    /// </summary>
    public const float MemoireDefautS = 40f;

    /// <summary>La fenetre de la transformee propre a la separation, en echantillons.</summary>
    public const int FenetreLog = 4096;

    /// <summary>
    /// Une image d'apprentissage sur deux. Un gabarit n'a pas besoin de 21 ms de finesse
    /// pour se definir, et cela divise par deux ce que l'apprentissage coute.
    /// </summary>
    private const int SousEchantillon = 2;

    private readonly int _memoire;

    /// <summary>
    /// Un premier apprentissage rapide, pour que l'ecran ne reste pas noir quarante
    /// secondes. Il cherche un nombre de sources PROVISOIRE, et sera remplace des que la
    /// memoire est pleine par l'apprentissage qui, lui, choisit.
    /// </summary>
    private const int Provisoire = 128;
    private const int SourcesProvisoires = 4;
    private bool _provisoireFait;
    private bool _choixFait;

    /// <summary>
    /// Combien de sources la separation publie EN CE MOMENT — decouvert, pas impose.
    ///
    /// « Des fois on en a 2, des fois 8, c'est justement ce que le programme est cense me
    /// dire. » Zero tant que rien n'a ete appris ; puis le nombre retenu par le balayage.
    /// Les rangs au-dela rendent zero partout.
    /// </summary>
    public int Actives { get; private set; }

    /// <summary>Ce que le balayage a mesure, pour la sonde et le diagnostic.</summary>
    public IReadOnlyList<ProfileLearner.Bilan> Bilans => _apprentissage.Bilans;
    public bool ChoixFait => _choixFait;

    /// <summary>Iterations de l'apprentissage complet. Au-dela, les gabarits ne bougent plus guere.</summary>
    private const int IterationsApprentissage = 60;

    /// <summary>
    /// Iterations de chaque essai du balayage. A vingt, mesure sur Passepartout, le reste ne
    /// baissait pas de facon monotone d'un K au suivant (1,0 · 0,3 · 0,7 · 0,3 point) : le
    /// hasard de l'amorcage pesait autant que la source ajoutee. A quarante, il se lit.
    /// </summary>
    private const int IterationsBalayage = 40;

    /// <summary>Iterations du suivi, sur la seule image courante.</summary>
    private const int IterationsSuivi = 8;

    private const float Eps = 1e-9f;
    private const int NLog = ProfileLearner.NLog;
    private const int Positions = ProfileLearner.Positions;
    private const int Longueur = ProfileLearner.Longueur;

    private readonly int _rate;
    private readonly int _hop;
    private readonly float[] _w;          // gabarits : Sources x Longueur
    private readonly float[] _v;          // spectrogramme glissant : memoire x NLog
    private readonly float[] _courant;    // niveau de chaque source sur l'image courante
    private readonly float[] _hCourant;   // niveaux par position, image courante
    private readonly float[] _vh;         // reconstruction de l'image courante
    private readonly float[] _spectre;    // l'image courante, sur l'axe log

    // La transformee propre a la separation.
    private readonly float[] _anneau = new float[FenetreLog];
    private int _anneauEcrit;
    private int _depuisTrame;
    private readonly float[] _hann = Fft.Hann(FenetreLog);
    private readonly float[] _re = new float[FenetreLog];
    private readonly float[] _im = new float[FenetreLog];
    private readonly int[] _fbDebut = new int[NLog];
    private readonly float[][] _fbPoids = new float[NLog][];

    private int _ecrit;
    private int _remplies;
    private int _trame;
    private int _depuisApprentissage;
    private readonly Random _alea = new(1203);

    /// <summary>Niveau de chaque source sur l'image courante.</summary>
    public IReadOnlyList<float> Activations => _courant;

    /// <summary>A-t-on appris des gabarits, ou rend-on encore du bruit ?</summary>
    public bool Pret { get; private set; }

    /// <summary>Longueur d'un gabarit, en cases logarithmiques.</summary>
    public int Bins => Longueur;

    /// <summary>
    /// Le gabarit d'une source, dans l'ordre du grave a l'aigu. Lecture seule.
    ///
    /// POURQUOI IL SORT D'ICI. Toutes les mesures de ce projet disent si une source est
    /// REGULIERE ; aucune ne dit si elle contient ce qu'elle pretend contenir. Un gabarit
    /// exporte permet de reconstruire ce que la source a retenu, en son, et de l'ECOUTER —
    /// ce que l'oreille tranche en dix secondes et qu'aucun chiffre n'a su dire.
    /// </summary>
    public void ProfilOrdonne(int rang, Span<float> sortie)
    {
        if ((uint)rang >= Actives || sortie.Length < Longueur) return;
        _w.AsSpan(_ordre[rang] * Longueur, Longueur).CopyTo(sortie);
    }

    /// <summary>
    /// Hauteur habituelle de chaque source, sur une echelle d'octaves entre 0 et 1 : la
    /// couleur du gabarit plus la position ou il a joue en moyenne. Sert a ordonner les
    /// sources du grave a l'aigu, faute de savoir les nommer.
    /// </summary>
    public IReadOnlyList<float> Hauteurs => _hauteurs;

    private readonly float[] _hauteurs = new float[Sources];
    private readonly float[] _centres = new float[Sources];         // couleur du gabarit, en cases
    private readonly float[] _positionsApprises = new float[Sources];
    private readonly float[] _positionCourante = new float[Sources];
    private readonly int[] _ordre = new int[Sources];

    /// <summary>
    /// A quel point le gabarit de chaque source tient d'un apprentissage a l'autre, 0 a 1.
    /// Une source dont le gabarit se retrouve identique est un objet stable du morceau ; un
    /// gabarit qui change a chaque fois melange plusieurs choses.
    /// </summary>
    private readonly float[] _stabilite = new float[Sources];
    private readonly float[] _profilPrecedent;
    private bool _profilConnu;

    /// <summary>Stabilite du gabarit de la source de rang donne, du grave a l'aigu.</summary>
    public float StabiliteOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? _stabilite[_ordre[rang]] : 0f;

    /// <summary>
    /// Combien d'images cette source a passe a jouer. C'est le pendant de la stabilite :
    /// l'une dit si la source est un objet net, l'autre si on l'a assez vue pour en juger.
    /// </summary>
    private const int Assez = 200;
    private readonly int[] _vues = new int[Sources];

    /// <summary>Sous ce niveau relatif, la source ne joue pas et n'apprend rien d'elle.</summary>
    private const float Audible = 0.12f;

    public float EcouteOrdonnee(int rang) =>
        rang >= 0 && rang < Actives
            ? MathF.Min(1f, _vues[_ordre[rang]] / (float)Assez)
            : 0f;

    /// <param name="hop">Echantillons entre deux images d'analyse.</param>
    /// <param name="memoire">Images d'analyse gardees pour apprendre ; zero = quarante secondes.</param>
    public SourceSeparator(int sampleRate = 48_000, int hop = 1024, int memoire = 0)
    {
        _rate = sampleRate;
        _hop = hop;
        var images = memoire > 0 ? memoire : (int)(MemoireDefautS * sampleRate / hop);
        _memoire = Math.Max(Provisoire, images / SousEchantillon);
        _w = new float[Sources * Longueur];
        _v = new float[_memoire * NLog];
        _courant = new float[Sources];
        _hCourant = new float[Sources * Positions];
        _vh = new float[NLog];
        _spectre = new float[NLog];
        _profilPrecedent = new float[Sources * Longueur];

        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
        for (var s = 0; s < Sources; s++) _ordre[s] = s;
        ConstruireProjection();

        _apprentissage = new ProfileLearner(Sources, _memoire, IterationsApprentissage);
    }

    private readonly ProfileLearner _apprentissage;

    /// <summary>
    /// La projection des raies lineaires sur les cases logarithmiques : un triangle par case,
    /// large d'une case ou d'une raie, la plus grande des deux. Dans le grave, ou les raies
    /// sont plus espacees que les cases, chaque case interpole entre ses deux raies ; dans
    /// l'aigu, ou elles sont plus serrees, chaque case en moyenne plusieurs.
    /// </summary>
    private void ConstruireProjection()
    {
        var raieHz = _rate / (float)FenetreLog;
        var raies = FenetreLog / 2 + 1;
        var ratio = MathF.Pow(2f, 1f / ProfileLearner.ParOctave) - 1f;
        for (var l = 0; l < NLog; l++)
        {
            var fc = ProfileLearner.F0 * MathF.Pow(2f, l / (float)ProfileLearner.ParOctave);
            var demi = MathF.Max(fc * ratio, raieHz);
            var debut = Math.Max(0, (int)MathF.Ceiling((fc - demi) / raieHz));
            var fin = Math.Min(raies - 1, (int)MathF.Floor((fc + demi) / raieHz));
            var poids = new float[Math.Max(0, fin - debut + 1)];
            float somme = 0;
            for (var b = debut; b <= fin; b++)
            {
                var p = 1f - MathF.Abs(b * raieHz - fc) / demi;
                if (p < 0f) p = 0f;
                poids[b - debut] = p;
                somme += p;
            }
            if (somme > Eps) for (var i = 0; i < poids.Length; i++) poids[i] /= somme;
            _fbDebut[l] = debut;
            _fbPoids[l] = poids;
        }
    }

    /// <summary>
    /// Des echantillons, dans l'ordre. A chaque <c>hop</c> echantillons, une image est prise
    /// sur les 4096 derniers, projetee sur l'axe logarithmique, et suivie.
    /// </summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        foreach (var x in samples)
        {
            _anneau[_anneauEcrit] = x;
            _anneauEcrit = (_anneauEcrit + 1) % FenetreLog;
            if (++_depuisTrame >= _hop)
            {
                _depuisTrame = 0;
                Trame();
            }
        }
    }

    private void Trame()
    {
        // La transformee des 4096 derniers echantillons, fenetres.
        for (var i = 0; i < FenetreLog; i++)
        {
            _re[i] = _anneau[(_anneauEcrit + i) % FenetreLog] * _hann[i];
            _im[i] = 0f;
        }
        Fft.Forward(_re, _im);
        var raies = FenetreLog / 2 + 1;
        // La magnitude, reutilisee en place dans _re.
        for (var b = 0; b < raies; b++) _re[b] = MathF.Sqrt(_re[b] * _re[b] + _im[b] * _im[b]);
        for (var l = 0; l < NLog; l++)
        {
            var poids = _fbPoids[l];
            var debut = _fbDebut[l];
            float somme = 0;
            for (var i = 0; i < poids.Length; i++) somme += poids[i] * _re[debut + i];
            _spectre[l] = somme;
        }

        if (_trame++ % SousEchantillon == 0) Memoriser();
        Suivre();

        // Une source ne compte comme vue que quand elle joue. Le maximum sert de reference :
        // une source discrete mais presente doit compter, une source a zero non.
        var fort = 1e-4f;
        for (var i = 0; i < Actives; i++) fort = MathF.Max(fort, _courant[i]);
        for (var i = 0; i < Actives; i++)
            if (_courant[i] / fort > Audible && _vues[i] < Assez) _vues[i]++;
    }

    /// <summary>Range l'image dans la memoire, et lance ce qui doit l'etre.</summary>
    private void Memoriser()
    {
        _spectre.AsSpan().CopyTo(_v.AsSpan(_ecrit * NLog, NLog));
        _ecrit = (_ecrit + 1) % _memoire;
        if (_remplies < _memoire) _remplies++;

        // D'ABORD UN PROVISOIRE, VITE ; PUIS LE VRAI, QUAND ON A ENTENDU ASSEZ.
        //
        // Les quarante secondes sont le prix d'un apprentissage qui distingue des
        // instruments. Mais quarante secondes d'ecran noir a chaque disque ne se defendent
        // pas : on apprend donc une premiere fois tot, a un nombre de sources provisoire, et
        // l'on remplace tout des que la memoire est pleine.
        var provisoire = Math.Min(Provisoire, _memoire / 2);
        if (!_provisoireFait && _remplies >= provisoire)
        {
            if (_apprentissage.TryStart(_v, _w, SourcesProvisoires, provisoire))
                _provisoireFait = true;
        }
        else if (_remplies >= _memoire && (!_choixFait || ++_depuisApprentissage >= _memoire / 2))
        {
            // Le CHOIX du nombre de sources se fait une fois par disque, DES QUE la memoire
            // est pleine : il attendait encore une demi-memoire de plus, et les pistes en
            // solo n'arrivaient qu'a deux minutes vingt — le DJ fermait la fenetre avant,
            // deux fois de suite. Ensuite on reapprend au nombre retenu, une fois par
            // demi-memoire, en repartant des gabarits courants : ils restent a leur place.
            var lance = _choixFait
                ? _apprentissage.TryStart(_v, _w, Actives, _memoire)
                : _apprentissage.TryStartChoix(_v, 2, Sources, _memoire, IterationsBalayage);
            if (lance) _depuisApprentissage = 0;
        }

        // Les gabarits fraichement appris sont repris ici, entre deux images, sur le fil
        // d'analyse : c'est le seul instant ou ils changent.
        if (_apprentissage.TryAdopt(_w, _positionsApprises))
        {
            var avant = Actives;
            Actives = _apprentissage.DernierK;
            if (_apprentissage.DernierEtaitChoix) _choixFait = true;
            // Un nombre de sources qui change rend la stabilite par rang sans objet : les
            // rangs ne designent plus les memes choses. On repart.
            if (Actives != avant) _profilConnu = false;
            Ordonner();
            MesurerStabilite();
            Pret = true;
        }
    }

    /// <summary>Fait apprendre sur le fil d'analyse. Pour la mesure et les tests.</summary>
    public bool ApprentissageEnLigne
    {
        get => _apprentissage.RunInline;
        set => _apprentissage.RunInline = value;
    }

    /// <summary>Ce que l'apprentissage a coute, pour la sonde.</summary>
    public double ApprentissageMs => _apprentissage.AverageMs;
    public double ApprentissagePireMs => _apprentissage.WorstMs;
    public int Apprentissages => _apprentissage.Runs;

    public void Reset()
    {
        Array.Clear(_v);
        Array.Clear(_vues);
        Array.Clear(_anneau);
        _profilConnu = false;
        Array.Clear(_stabilite);
        _remplies = _ecrit = _depuisApprentissage = _trame = 0;
        Pret = false;
        Actives = 0;
        _provisoireFait = false;
        _choixFait = false;
        Array.Clear(_courant);
        Array.Clear(_hCourant);
        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
    }

    /// <summary>
    /// Suit l'image courante, gabarits figes : pour chaque source, a quelle position et a
    /// quel niveau elle joue. La regle de Kullback-Leibler, sur une seule colonne.
    /// </summary>
    private void Suivre()
    {
        if (Actives <= 0) return;
        // On repart des niveaux de l'image d'avant, planches a un minimum : une position qui
        // etait a zero doit pouvoir revenir quand la note revient.
        for (var i = 0; i < Actives * Positions; i++) if (_hCourant[i] < 1e-4f) _hCourant[i] = 1e-4f;
        Array.Clear(_hCourant, Actives * Positions, (Sources - Actives) * Positions);

        for (var it = 0; it < IterationsSuivi; it++)
        {
            _vh.AsSpan().Fill(Eps);
            for (var s = 0; s < Actives; s++)
            {
                var w = _w.AsSpan(s * Longueur, Longueur);
                for (var p = 0; p < Positions; p++)
                    ProfileLearner.Ajouter(_vh.AsSpan(p, Longueur), w, _hCourant[s * Positions + p]);
            }
            for (var f = 0; f < NLog; f++) _vh[f] = (_spectre[f] + Eps) / _vh[f];
            for (var s = 0; s < Actives; s++)
            {
                var w = _w.AsSpan(s * Longueur, Longueur);
                for (var p = 0; p < Positions; p++)
                    _hCourant[s * Positions + p] *= ProfileLearner.Produit(w, _vh.AsSpan(p, Longueur));
            }
        }

        for (var s = 0; s < Sources; s++)
        {
            if (s >= Actives) { _courant[s] = 0f; continue; }
            double niveau = 0, poids = 0;
            for (var p = 0; p < Positions; p++)
            {
                var h = _hCourant[s * Positions + p];
                niveau += h;
                poids += h * p;
            }
            _courant[s] = (float)niveau;
            if (niveau > Eps) _positionCourante[s] = (float)(poids / niveau);
        }
    }

    /// <summary>
    /// Range les sources du grave a l'aigu, par la couleur de leur gabarit et la position
    /// ou il a joue. Sans nom, il faut au moins un ordre stable : sans lui, la source
    /// affichee en premiere case changerait a chaque apprentissage.
    /// </summary>
    private void Ordonner()
    {
        for (var s = 0; s < Sources; s++)
        {
            double poids = 0, total = 0;
            var w = _w.AsSpan(s * Longueur, Longueur);
            for (var f = 0; f < Longueur; f++)
            {
                poids += w[f] * f;
                total += w[f];
            }
            _centres[s] = total > Eps ? (float)(poids / total) : Longueur / 2f;
            _hauteurs[s] = EnOctavesHz(CaseEnHz(_centres[s] + _positionsApprises[s]));
        }

        for (var s = 0; s < Sources; s++) _ordre[s] = s;
        // Seules les actives se classent du grave a l'aigu ; les rangs eteints restent
        // derriere, ou personne ne les lit.
        Array.Sort(_ordre, 0, Actives, Comparer<int>.Create((a, b) => _hauteurs[a].CompareTo(_hauteurs[b])));
    }

    /// <summary>
    /// Compare les gabarits fraichement appris a ceux d'avant, par cosinus : deux gabarits
    /// qui pointent dans la meme direction decrivent le meme timbre, quelle que soit leur
    /// intensite. Les colonnes restent a leur place parce que chaque apprentissage repart
    /// des gabarits courants au lieu de tirer au hasard.
    /// </summary>
    private void MesurerStabilite()
    {
        if (_profilConnu)
        {
            for (var s = 0; s < Sources; s++)
            {
                double ps = 0, na = 0, nb = 0;
                for (var f = 0; f < Longueur; f++)
                {
                    var a = _w[s * Longueur + f];
                    var b = _profilPrecedent[s * Longueur + f];
                    ps += a * b;
                    na += (double)a * a;
                    nb += (double)b * b;
                }
                var cos = na > Eps && nb > Eps
                    ? (float)(ps / (Math.Sqrt(na) * Math.Sqrt(nb)))
                    : 0f;
                // Un cosinus entre gabarits positifs vaut deja 0,5 pour deux timbres sans
                // rapport : la moitie basse de l'echelle ne distingue rien. On l'etire.
                var net = Clamp01((cos - 0.55f) / 0.40f);
                _stabilite[s] += (net - _stabilite[s]) * 0.35f;
            }
        }

        Array.Copy(_w, _profilPrecedent, _w.Length);
        _profilConnu = true;
    }

    /// <summary>Niveau de la source de rang <paramref name="rang"/>, du grave a l'aigu.</summary>
    public float ActivationOrdonnee(int rang) =>
        rang >= 0 && rang < Actives ? _courant[_ordre[rang]] : 0f;

    /// <summary>
    /// Hauteur de la source de rang donne, EN CE MOMENT : la couleur de son gabarit plus la
    /// position ou il joue sur cette image. C'est une vraie hauteur de note, ce que le
    /// profil fige ne pouvait pas donner.
    /// </summary>
    public float HauteurOrdonnee(int rang)
    {
        if (rang < 0 || rang >= Actives) return 0.5f;
        var s = _ordre[rang];
        var position = _courant[s] > Eps ? _positionCourante[s] : _positionsApprises[s];
        return EnOctavesHz(CaseEnHz(_centres[s] + position));
    }

    /// <summary>Une case de l'axe logarithmique, en hertz.</summary>
    public static float CaseEnHz(float caseLog) =>
        ProfileLearner.F0 * MathF.Pow(2f, caseLog / ProfileLearner.ParOctave);

    /// <summary>
    /// Grave et aigu du crate, en hertz. Sept octaves, ce qui couvre du sub au cymbale.
    /// </summary>
    private const float GraveHz = 40f, AiguHz = 10_000f;

    /// <summary>
    /// Convertit un centre de gravite spectral, exprime en fraction de la bande analysee, en
    /// une position perceptive entre 0 et 1 — en octaves, parce que l'oreille compte en
    /// rapports et non en ecarts. Mesure a l'ecran, en lineaire les six contours tenaient
    /// dans les treize pour cent du bas de leur case.
    /// </summary>
    public static float EnOctaves(float rangMoyen, int sampleRate) =>
        EnOctavesHz(rangMoyen * sampleRate * 0.5f);

    public static float EnOctavesHz(float hz)
    {
        if (hz <= GraveHz) return 0f;
        var octaves = MathF.Log2(hz / GraveHz) / MathF.Log2(AiguHz / GraveHz);
        return Clamp01(octaves);
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
