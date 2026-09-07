namespace Emotion.Signal;

/// <summary>
/// Separation harmonique / percussive par filtres medians.
///
/// L'idee tient en une observation sur le spectrogramme : les deux familles de sons y
/// laissent des traces perpendiculaires.
///
/// <code>
///   frequence
///      ^
///      |   |        |            une percussion : trace VERTICALE
///      |   |        |            large en frequence, breve dans le temps
///      |───────────────────      une note tenue : trace HORIZONTALE
///      |   |        |            etroite en frequence, longue dans le temps
///      +─────────────────────> temps
/// </code>
///
/// D'ou la methode : une mediane <b>le long du temps</b>, a frequence fixe, conserve ce
/// qui dure et efface ce qui passe — c'est l'harmonique. Une mediane <b>le long des
/// frequences</b>, a instant fixe, conserve ce qui s'etale et efface ce qui est etroit —
/// c'est le percussif.
///
/// La mediane et non la moyenne, parce qu'elle est insensible aux valeurs extremes :
/// c'est precisement ce qu'on veut, puisque l'autre composante <i>est</i> la valeur
/// extreme dont il faut se debarrasser.
///
/// Pourquoi cela sert ici : sur instamata, le piano remplit les mediums de flux en
/// permanence et brouille la detection des claps, pendant que les percussions salissent
/// le chromagramme. Separer les deux avant analyse nettoie les deux chaines d'un coup,
/// au lieu d'ajouter une correction a chacune.
///
/// <b>Le prix est une latence</b>, et elle est structurelle : pour savoir si un bin
/// durait, il faut avoir vu la suite. Elle vaut la moitie de la fenetre temporelle.
/// </summary>
public sealed class Hpss
{
    private readonly int _bins;
    private readonly int _timeFrames;
    private readonly int _freqBins;

    private readonly float[][] _history;      // spectres recents, tampon circulaire
    private int _write;
    private int _filled;

    private readonly float[] _harmonic;
    private readonly float[] _percussive;
    private readonly float[] _scratchTime;
    private readonly float[] _scratchFreq;

    /// <param name="bins">Nombre de bins du spectre, soit la moitie de la fenetre FFT.</param>
    /// <param name="timeFrames">
    /// Longueur de la mediane temporelle, en fenetres. Impair.
    ///
    /// Sept fenetres valent 149 ms d'observation et <b>64 ms de latence</b>, puisqu'il
    /// faut attendre la moitie de la fenetre pour juger celle du milieu. C'est le
    /// compromis retenu : plus long separe mieux mais fait arriver le visuel apres le
    /// son, ce qui se voit immediatement sur une frappe.
    /// </param>
    /// <param name="freqBins">
    /// Longueur de la mediane frequentielle, en bins. Impair. Dix-sept bins valent
    /// 800 Hz a 48 kHz sur une fenetre de 1024 : assez large pour qu'une raie de piano
    /// y soit minoritaire, assez etroit pour ne pas aplatir un roulement de caisse.
    /// </param>
    public Hpss(int bins, int timeFrames = 7, int freqBins = 17)
    {
        if (timeFrames % 2 == 0) throw new ArgumentException("longueur impaire attendue", nameof(timeFrames));
        if (freqBins % 2 == 0) throw new ArgumentException("longueur impaire attendue", nameof(freqBins));

        _bins = bins;
        _timeFrames = timeFrames;
        _freqBins = freqBins;

        _history = new float[timeFrames][];
        for (var i = 0; i < timeFrames; i++) _history[i] = new float[bins];

        _harmonic = new float[bins];
        _percussive = new float[bins];
        _scratchTime = new float[timeFrames];
        _scratchFreq = new float[freqBins];
    }

    /// <summary>Retard en fenetres entre l'entree et la sortie.</summary>
    public int LatencyFrames => _timeFrames / 2;

    /// <summary>La derniere composante harmonique separee.</summary>
    public ReadOnlySpan<float> Harmonic => _harmonic;

    /// <summary>La derniere composante percussive separee.</summary>
    public ReadOnlySpan<float> Percussive => _percussive;

    /// <summary>
    /// Absorbe un spectre et separe celui du <b>milieu</b> du tampon, donc celui d'il y a
    /// <see cref="LatencyFrames"/> fenetres.
    ///
    /// Rend faux tant que le tampon n'est pas plein : au demarrage, les composantes ne
    /// veulent rien dire et les livrer quand meme ferait declencher tout ce qui ecoute.
    /// </summary>
    public bool Feed(ReadOnlySpan<float> spectrum)
    {
        if (spectrum.Length != _bins)
            throw new ArgumentException($"{_bins} bins attendus", nameof(spectrum));

        spectrum.CopyTo(_history[_write]);
        _write = (_write + 1) % _timeFrames;
        if (_filled < _timeFrames) _filled++;
        if (_filled < _timeFrames) return false;

        // La fenetre du milieu : _write pointe la plus ancienne, on avance de la moitie.
        var middle = _history[(_write + _timeFrames / 2) % _timeFrames];

        var halfFreq = _freqBins / 2;

        for (var k = 0; k < _bins; k++)
        {
            // Mediane temporelle a frequence fixe : ce qui dure.
            for (var t = 0; t < _timeFrames; t++)
                _scratchTime[t] = _history[(_write + t) % _timeFrames][k];
            var h = Median(_scratchTime, _timeFrames);

            // Mediane frequentielle a instant fixe : ce qui s'etale. Les bords sont
            // repliques plutot que remplis de zeros, sinon le grave et l'aigu extremes
            // seraient artificiellement eteints.
            for (var j = 0; j < _freqBins; j++)
            {
                var idx = Math.Clamp(k - halfFreq + j, 0, _bins - 1);
                _scratchFreq[j] = middle[idx];
            }
            var p = Median(_scratchFreq, _freqBins);

            // Masques de Wiener plutot qu'un choix binaire. Un masque binaire attribue
            // chaque bin entier a l'une ou l'autre composante et laisse des trous nets
            // dans le spectre, qui s'entendent et se voient. Les masques doux repartissent
            // proportionnellement au carre, et leur somme vaut exactement l'original.
            var hh = h * h;
            var pp = p * p;
            var total = hh + pp;

            if (total < 1e-12f)
            {
                _harmonic[k] = 0f;
                _percussive[k] = 0f;
            }
            else
            {
                var v = middle[k];
                _harmonic[k] = v * (hh / total);
                _percussive[k] = v * (pp / total);
            }
        }

        return true;
    }

    /// <summary>
    /// Mediane par tri par insertion sur une copie. Sur sept ou dix-sept elements, le
    /// tri par insertion bat tout algorithme plus savant : pas d'allocation, pas
    /// d'indirection, et le tableau tient dans le cache.
    /// </summary>
    private static float Median(float[] buffer, int n)
    {
        for (var i = 1; i < n; i++)
        {
            var v = buffer[i];
            var j = i - 1;
            while (j >= 0 && buffer[j] > v) { buffer[j + 1] = buffer[j]; j--; }
            buffer[j + 1] = v;
        }
        return buffer[n / 2];
    }
}
