namespace Emotion.Signal;

/// <summary>
/// Separe le son en sources, sans savoir lesquelles.
///
/// LE PROBLEME QU'AUCUN FILTRE DE FREQUENCE NE RESOUT. Un piano et un saxophone qui jouent
/// dans la meme octave tombent dans la meme bande : quelle que soit la finesse du
/// decoupage, on additionne leurs deux niveaux et l'on obtient une grandeur qui ne decrit
/// ni l'un ni l'autre. Selim l'a dit ainsi : « piano et saxophone qui s'additionnent, ca
/// donne un truc illisible ».
///
/// Deux sons peuvent partager une hauteur ; ils ne partagent pas leur <b>timbre</b>. Un
/// piano et un saxophone sur le meme la n'ont pas les memes harmoniques, ni les memes
/// rapports entre elles. C'est cette signature-la qui les separe, et elle ne se voit pas
/// dans une bande de frequence.
///
/// LA METHODE. La factorisation en matrices non negatives decompose un spectrogramme V en
/// un produit W·H : W contient <see cref="Sources"/> profils spectraux — les timbres — et
/// H leurs activations dans le temps. Rien ne lui est enseigne : elle trouve les profils
/// qui expliquent le mieux ce qu'elle entend. C'est ce qui la rend juste ici, ou l'on veut
/// separer <b>sans nommer</b> : elle rend « une source », jamais « un piano ».
///
/// DEUX REGIMES, ET C'EST CE QUI LA REND UTILISABLE EN DIRECT.
///
///   apprendre    W et H ensemble, sur quelques secondes. Couteux, fait rarement, et
///                hors du temps de la salle — c'est le travail du cue.
///   suivre       W fige, H seul, sur l'image courante. Quelques milliers d'operations,
///                donc gratuit a l'echelle d'une fenetre d'analyse.
///
/// Selim autorise « des centaines de millisecondes » pour la separation, a condition
/// qu'elle soit juste. L'apprentissage les prend ; le suivi n'en prend aucune.
/// </summary>
public sealed class SourceSeparator
{
    /// <summary>
    /// Nombre de sources cherchees.
    ///
    /// Un morceau de ce repertoire en contient rarement davantage : une basse, une
    /// batterie, un ou deux instruments tenus, une voix, du souffle. En demander vingt
    /// decouperait un meme instrument en morceaux ; en demander deux les melangerait.
    ///
    /// SIX EST MESURE, PAS SUPPOSE — ET L'INTUITION INVERSE EST FAUSSE.
    ///
    /// On pourrait croire qu'en demander davantage separerait mieux. Sur un morceau du
    /// crate, la stabilite des profils va dans l'autre sens : 0,87 a 0,99 avec six sources,
    /// 0,85 a 0,92 avec neuf, 0,76 a 0,92 avec douze. Passe un certain point, la
    /// factorisation n'a plus d'objets a trouver et se met a couper des instruments en
    /// morceaux — des morceaux qui ne se retrouvent pas d'un apprentissage a l'autre.
    ///
    /// Le cout, lui, monte franchement : 167 ms par apprentissage a six, 355 a douze. On
    /// paierait donc deux fois pour un resultat moins bon.
    /// </summary>
    public const int Sources = 6;

    /// <summary>Images gardees pour l'apprentissage. A 21 ms, cela fait 2,7 secondes.</summary>
    private const int Memoire = 128;

    /// <summary>Iterations de l'apprentissage complet. Au-dela, W ne bouge plus guere.</summary>
    private const int IterationsApprentissage = 40;

    /// <summary>Iterations du suivi, sur la seule image courante.</summary>
    private const int IterationsSuivi = 6;

    private const float Eps = 1e-9f;

    private readonly int _bins;
    private readonly float[] _w;          // profils spectraux : bins x Sources
    private readonly float[] _v;          // spectrogramme glissant : bins x Memoire
    private readonly float[] _courant;    // activations de l'image courante
    private readonly float[] _numer;
    private readonly float[] _denom;
    private readonly float[] _wh;

    private int _ecrit;
    private int _remplies;
    private int _depuisApprentissage;
    private readonly Random _alea = new(1203);

    /// <summary>Activation de chaque source sur l'image courante, normalisee.</summary>
    public IReadOnlyList<float> Activations => _courant;

    /// <summary>A-t-on appris des profils, ou rend-on encore du bruit ?</summary>
    public bool Pret { get; private set; }

    /// <summary>
    /// Centre de gravite spectral de chaque profil, en fraction de la bande analysee.
    /// Sert a ordonner les sources du grave a l'aigu, faute de savoir les nommer.
    /// </summary>
    public IReadOnlyList<float> Hauteurs => _hauteurs;

    private readonly float[] _hauteurs = new float[Sources];
    private readonly int[] _ordre = new int[Sources];

    /// <summary>
    /// A quel point le profil de chaque source tient d'un apprentissage a l'autre, 0 a 1.
    ///
    /// C'EST LA BONNE MESURE DE NETTETE, ET L'ANCIENNE DECRIVAIT AUTRE CHOSE.
    ///
    /// La nettete etait calculee sur les <b>bandes de frequence</b> — six octaves — alors
    /// que ce qui est affiche a l'ecran, ce sont les six sources separees par le
    /// <b>timbre</b>. Deux grandeurs differentes portaient le meme numero : la jauge de la
    /// case 1 decrivait la tranche 100-200 Hz pendant que la forme de la case 1 dessinait
    /// un timbre. Une bande d'octave est presque toujours partagee — un kick et une basse y
    /// tombent ensemble — donc la jauge restait basse quoi qu'il arrive, et disait le
    /// contraire de ce que l'oeil voyait.
    ///
    /// Une source NMF, elle, est definie par son profil spectral : c'est exactement son
    /// timbre. Si ce profil se retrouve identique d'un apprentissage au suivant, la source
    /// est un objet stable du morceau ; s'il change a chaque fois, la factorisation n'a pas
    /// trouve d'objet et melange plusieurs choses.
    /// </summary>
    private readonly float[] _stabilite = new float[Sources];
    private readonly float[] _profilPrecedent;
    private bool _profilConnu;

    /// <summary>Stabilite du profil de la source de rang donne, du grave a l'aigu.</summary>
    public float StabiliteOrdonnee(int rang) =>
        rang >= 0 && rang < Sources ? _stabilite[_ordre[rang]] : 0f;

    /// <summary>
    /// Combien d'images cette source a passe a jouer. C'est le pendant de la stabilite :
    /// l'une dit si la source est un objet net, l'autre si on l'a assez vue pour en juger.
    ///
    /// ELLE DOIT SE COMPTER SUR LA SOURCE, PAS SUR LA BANDE DE FREQUENCE. Les deux
    /// grandeurs publiees decrivaient encore deux objets differents : la nettete portait
    /// sur le timbre separe, l'ecoute sur la tranche d'octave du meme rang. Une source qui
    /// n'entre qu'au refrain se serait donc declaree « assez ecoutee » parce que sa bande
    /// de frequence, elle, contenait du son en permanence.
    /// </summary>
    private const int Assez = 200;
    private readonly int[] _vues = new int[Sources];

    /// <summary>Sous ce niveau relatif, la source ne joue pas et n'apprend rien d'elle.</summary>
    private const float Audible = 0.12f;

    public float EcouteOrdonnee(int rang) =>
        rang >= 0 && rang < Sources
            ? MathF.Min(1f, _vues[_ordre[rang]] / (float)Assez)
            : 0f;

    public SourceSeparator(int bins)
    {
        _bins = bins;
        _w = new float[bins * Sources];
        _v = new float[bins * Memoire];
        _courant = new float[Sources];
        _numer = new float[Sources];
        _denom = new float[Sources];
        _wh = new float[bins];
        _profilPrecedent = new float[bins * Sources];

        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
        for (var s = 0; s < Sources; s++) _ordre[s] = s;

        _apprentissage = new ProfileLearner(bins, Sources, Memoire, IterationsApprentissage);
    }

    /// <summary>Une image de spectre. Rend les activations de l'image, dans l'ordre grave a aigu.</summary>
    private readonly ProfileLearner _apprentissage;

    public void Feed(ReadOnlySpan<float> spectre)
    {
        var n = Math.Min(_bins, spectre.Length);
        var col = _ecrit;
        for (var i = 0; i < n; i++) _v[i * Memoire + col] = spectre[i];

        _ecrit = (_ecrit + 1) % Memoire;
        if (_remplies < Memoire) _remplies++;

        // L'apprentissage ne tourne qu'une fois par memoire pleine : c'est lui qui coute,
        // et il n'a aucune raison d'etre refait a chaque image.
        if (_remplies >= Memoire && ++_depuisApprentissage >= Memoire / 2)
        {
            // On ne calcule plus ici : on demande. Si l'apprentissage precedent tourne
            // encore, la demande est refusee et l'on garde les profils actuels — sauter un
            // apprentissage ne se voit pas, bloquer treize images se voit.
            if (_apprentissage.TryStart(_v, _w)) _depuisApprentissage = 0;
        }

        // Les profils fraichement appris sont repris ici, entre deux images, sur le fil
        // d'analyse : c'est le seul instant ou W change, et le suivi ne peut donc jamais
        // tomber sur des profils a moitie ecrits.
        if (_apprentissage.TryAdopt(_w))
        {
            Ordonner();
            MesurerStabilite();
            Pret = true;
        }

        Suivre(spectre, n);

        // Une source ne compte comme vue que quand elle joue. Le maximum sert de reference :
        // une source discrete mais presente doit compter, une source a zero non.
        var fort = 1e-4f;
        for (var i = 0; i < Sources; i++) fort = MathF.Max(fort, _courant[i]);
        for (var i = 0; i < Sources; i++)
            if (_courant[i] / fort > Audible && _vues[i] < Assez) _vues[i]++;
    }

    /// <summary>Fait apprendre sur le fil d'analyse. Pour la mesure comparative seulement.</summary>
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
        _profilConnu = false;
        Array.Clear(_stabilite);
        _remplies = _ecrit = _depuisApprentissage = 0;
        Pret = false;
        for (var i = 0; i < _w.Length; i++) _w[i] = 0.1f + (float)_alea.NextDouble() * 0.9f;
    }

    /// <summary>
    /// Suit l'image courante, W fige. C'est ce qui rend la methode utilisable en direct :
    /// quelques milliers d'operations la ou l'apprentissage en demande des millions.
    /// </summary>
    private void Suivre(ReadOnlySpan<float> spectre, int n)
    {
        for (var s = 0; s < Sources; s++) if (_courant[s] <= 0f) _courant[s] = 0.1f;

        for (var it = 0; it < IterationsSuivi; it++)
        {
            for (var i = 0; i < n; i++)
            {
                float wh = 0;
                for (var s = 0; s < Sources; s++) wh += _w[i * Sources + s] * _courant[s];
                _wh[i] = wh;
            }

            Array.Clear(_numer);
            Array.Clear(_denom);

            for (var i = 0; i < n; i++)
            {
                var v = spectre[i];
                var wh = _wh[i];
                for (var s = 0; s < Sources; s++)
                {
                    var w = _w[i * Sources + s];
                    _numer[s] += w * v;
                    _denom[s] += w * wh;
                }
            }

            for (var s = 0; s < Sources; s++)
                _courant[s] *= _numer[s] / (_denom[s] + Eps);
        }
    }

    /// <summary>
    /// Range les sources du grave a l'aigu, par le centre de gravite de leur profil.
    ///
    /// Sans nom, il faut au moins un ordre stable : sans lui, la source affichee en
    /// premiere case changerait a chaque apprentissage, et l'oeil ne pourrait rien
    /// apprendre. La hauteur du timbre est le seul classement que le signal fournisse
    /// tout seul.
    /// </summary>
    private void Ordonner()
    {
        for (var s = 0; s < Sources; s++)
        {
            double poids = 0, total = 0;
            for (var i = 0; i < _bins; i++)
            {
                var w = _w[i * Sources + s];
                poids += w * i;
                total += w;
            }

            _hauteurs[s] = total > Eps ? (float)(poids / total) / _bins : 0.5f;
        }

        for (var s = 0; s < Sources; s++) _ordre[s] = s;
        Array.Sort(_ordre, (a, b) => _hauteurs[a].CompareTo(_hauteurs[b]));
    }

    /// <summary>
    /// Compare les profils fraichement appris a ceux d'avant.
    ///
    /// La mesure est un cosinus : deux profils qui pointent dans la meme direction
    /// decrivent le meme timbre, quelle que soit leur intensite. C'est ce qu'on veut — une
    /// source peut jouer plus fort sans changer de nature, et une mesure sensible a
    /// l'amplitude confondrait les deux.
    /// </summary>
    private void MesurerStabilite()
    {
        if (_profilConnu)
        {
            for (var s = 0; s < Sources; s++)
            {
                double ps = 0, na = 0, nb = 0;
                for (var i = 0; i < _bins; i++)
                {
                    var a = _w[i * Sources + s];
                    var b = _profilPrecedent[i * Sources + s];
                    ps += a * b;
                    na += (double)a * a;
                    nb += (double)b * b;
                }

                // LES COLONNES RESTENT A LEUR PLACE, ET CE N'EST PAS UN HASARD.
                //
                // Rien dans la factorisation n'impose que la source « 2 » d'un
                // apprentissage soit la source « 2 » du suivant : les colonnes pourraient
                // permuter, et l'on comparerait alors deux timbres sans rapport. Ce qui les
                // fixe, c'est l'amorcage — chaque apprentissage repart des profils courants
                // au lieu de tirer au hasard, donc il les raffine au lieu de les
                // redistribuer. La mesure le confirme : les stabilites relevees sont entre
                // 0,85 et 0,99, ce qu'une permutation ferait immediatement chuter.
                var cos = na > Eps && nb > Eps
                    ? (float)(ps / (Math.Sqrt(na) * Math.Sqrt(nb)))
                    : 0f;

                // Un cosinus entre profils positifs vaut deja 0,5 pour deux timbres sans
                // rapport : la moitie basse de l'echelle ne distingue rien. On l'etire
                // pour que la jauge parle de ce qui varie vraiment.
                var net = Clamp01((cos - 0.55f) / 0.40f);

                // Lissage : un seul apprentissage malchanceux ne doit pas effacer un
                // verdict construit sur plusieurs.
                _stabilite[s] += (net - _stabilite[s]) * 0.35f;
            }
        }

        Array.Copy(_w, _profilPrecedent, _w.Length);
        _profilConnu = true;
    }

    /// <summary>Activation de la source de rang <paramref name="rang"/>, du grave a l'aigu.</summary>
    public float ActivationOrdonnee(int rang) =>
        rang >= 0 && rang < Sources ? _courant[_ordre[rang]] : 0f;

    /// <summary>Hauteur du timbre de la source de rang donne.</summary>
    public float HauteurOrdonnee(int rang) =>
        rang >= 0 && rang < Sources ? _hauteurs[_ordre[rang]] : 0.5f;

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
