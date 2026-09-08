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

    public SourceSeparator(int bins)
    {
        _bins = bins;
        _w = new float[bins * Sources];
        _v = new float[bins * Memoire];
        _courant = new float[Sources];
        _numer = new float[Sources];
        _denom = new float[Sources];
        _wh = new float[bins];

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
            Pret = true;
        }

        Suivre(spectre, n);
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

    /// <summary>Activation de la source de rang <paramref name="rang"/>, du grave a l'aigu.</summary>
    public float ActivationOrdonnee(int rang) =>
        rang >= 0 && rang < Sources ? _courant[_ordre[rang]] : 0f;

    /// <summary>Hauteur du timbre de la source de rang donne.</summary>
    public float HauteurOrdonnee(int rang) =>
        rang >= 0 && rang < Sources ? _hauteurs[_ordre[rang]] : 0.5f;
}
