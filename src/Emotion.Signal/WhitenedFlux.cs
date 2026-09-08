namespace Emotion.Signal;

/// <summary>
/// Flux spectral apres blanchiment adaptatif, raie par raie.
///
/// LE PROBLEME QU'IL TRAITE, ET IL EST PROPRE A CE REPERTOIRE.
///
/// Le kick est juge sur trois bandes d'octave. A 44,1 kHz avec une fenetre de 1024
/// echantillons, une raie fait 43 Hz — ces trois bandes couvrent donc <b>environ trois
/// raies</b>, de 43 a 172 Hz. Or c'est exactement la ou vit la basse. Un kick et une note
/// de basse tombent dans les memes trois raies, et la somme des energies ne peut pas les
/// separer : la basse, plus forte et continue, decide de tout.
///
/// Elargir la tranche a ete essaye et rejete — en pleine bande, le detecteur voyait quatre
/// attaques par temps, noyees dans le souffle de bande et le crepitement du vinyle. Mais
/// ce n'est pas la largeur qui echouait, c'est la <b>sommation d'energies brutes</b> : une
/// raie forte et constante pese autant qu'une raie faible qui vient d'apparaitre, alors que
/// seule la seconde est une attaque.
///
/// CE QUE FAIT LE BLANCHIMENT.
///
/// Chaque raie est divisee par sa propre crete recente avant d'etre comparee a la fenetre
/// precedente. Une raie ou la basse tient une note est donc a 1 en permanence et ne varie
/// plus ; une raie ou quelque chose vient d'apparaitre monte de 0 vers 1 et compte plein.
/// Toutes les raies contribuent alors a la meme echelle, quel que soit leur niveau absolu,
/// et l'on peut regarder large sans se faire noyer.
///
/// C'est le « adaptive whitening » de Stowell et Plumbley, 2007.
///
/// LE PLANCHER N'EST PAS UN DETAIL. Sans lui, une raie vide serait divisee par presque
/// rien et son bruit de fond deviendrait une attaque pleine echelle a chaque fenetre. Il
/// est pris en fraction de la plus forte crete du moment : ce qui est mille fois plus
/// faible que le plus fort du morceau n'a pas voix au chapitre.
/// </summary>
public sealed class WhitenedFlux
{
    private readonly float[] _crete;
    private readonly float[] _blancPrec;
    private bool _amorce;

    /// <summary>
    /// Descente de la crete par fenetre.
    ///
    /// 0,9970 a quarante-sept fenetres par seconde : une crete oubliee met une demi-douzaine
    /// de secondes a se resorber. Plus vif, la reference suivrait le signal et il n'y aurait
    /// plus de contraste ; plus lent, un passage fort eteindrait la detection longtemps
    /// apres lui.
    /// </summary>
    private const float Descente = 0.9970f;

    /// <summary>Part de la plus forte crete en dessous de laquelle une raie ne compte pas.</summary>
    private const float Plancher = 0.001f;

    private float _moyenne;
    private const float Inertie = 0.02f;

    /// <summary>Le rapport de la derniere fenetre a la moyenne recente, sans dimension.</summary>
    public float Rapport { get; private set; }

    /// <summary>La derniere somme brute, pour le diagnostic.</summary>
    public float DernierTotal { get; private set; }

    public WhitenedFlux(int bins)
    {
        _crete = new float[bins];
        _blancPrec = new float[bins];
    }

    /// <summary>
    /// Une fenetre de spectre. Ne regarde que les raies de <paramref name="depuis"/> a
    /// <paramref name="jusqua"/>, exclue.
    /// </summary>
    public float Feed(ReadOnlySpan<float> spectre, int depuis, int jusqua)
    {
        var plusForte = 0f;
        for (var i = depuis; i < jusqua && i < _crete.Length; i++)
        {
            _crete[i] *= Descente;
            if (spectre[i] > _crete[i]) _crete[i] = spectre[i];
            if (_crete[i] > plusForte) plusForte = _crete[i];
        }

        var sol = plusForte * Plancher;
        var total = 0f;

        for (var i = depuis; i < jusqua && i < _crete.Length; i++)
        {
            var blanc = spectre[i] / MathF.Max(_crete[i], sol);
            if (_amorce)
            {
                var d = blanc - _blancPrec[i];
                if (d > 0f) total += d;
            }
            _blancPrec[i] = blanc;
        }

        _amorce = true;
        DernierTotal = total;

        _moyenne += (total - _moyenne) * Inertie;
        Rapport = _moyenne > 1e-6f ? total / _moyenne : 0f;
        return total;
    }

    public void Reset()
    {
        Array.Clear(_crete);
        Array.Clear(_blancPrec);
        _amorce = false;
        _moyenne = 0f;
        Rapport = 0f;
    }
}
