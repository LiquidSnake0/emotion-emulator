namespace Emotion.Signal;

/// <summary>
/// Decide si une montee du flux spectral est une attaque.
///
/// Le seuil est <b>adaptatif</b>, et c'est indispensable : un seuil fixe marcherait sur
/// un morceau et raterait tout le suivant, puisque le crate va d'un ambient feutre a des
/// batteries seches. On compare donc chaque valeur a la moyenne recente plutot qu'a une
/// constante — le detecteur suit le morceau au lieu d'etre regle pour lui.
/// </summary>
public sealed class OnsetDetector
{
    private const int History = 43;         // ~0,9 s a 48 kHz par fenetres de 1024
    private readonly float[] _recent = new float[History];
    private int _n;
    private int _sinceLast = int.MaxValue;

    /// <summary>
    /// Combien de fenetres au minimum entre deux attaques. A 48 kHz par fenetres de
    /// 1024, six fenetres valent 128 ms, soit 470 BPM : bien au-dela du crate, mais
    /// assez pour ne pas compter deux fois la meme frappe a cause de sa resonance.
    /// </summary>
    private const int MinGap = 6;

    /// <summary>
    /// Marge au-dessus de la moyenne recente. Trop bas, chaque nappe declenche ;
    /// trop haut, un morceau feutre ne declenche jamais.
    /// </summary>
    private const float Margin = 1.55f;

    /// <summary>Nourrit le detecteur d'une valeur de flux et dit si une attaque tombe ici.</summary>
    public bool Feed(float flux)
    {
        _sinceLast = _sinceLast == int.MaxValue ? MinGap : _sinceLast + 1;

        var mean = Mean();
        Push(flux);

        // Tant que l'historique n'est pas rempli, on ne decide rien : les premieres
        // fenetres apres le lancement declencheraient toutes.
        if (_n < History) return false;
        if (_sinceLast < MinGap) return false;
        if (mean <= 0f) return false;

        if (flux > mean * Margin)
        {
            _sinceLast = 0;
            return true;
        }
        return false;
    }

    private void Push(float v)
    {
        _recent[_n % History] = v;
        if (_n < int.MaxValue) _n++;
    }

    private float Mean()
    {
        var count = Math.Min(_n, History);
        if (count == 0) return 0f;
        var sum = 0f;
        for (var i = 0; i < count; i++) sum += _recent[i];
        return sum / count;
    }
}
