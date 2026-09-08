namespace Emotion.Signal;

/// <summary>
/// Fonction de detection d'attaque dans le domaine complexe.
///
/// POURQUOI LE FLUX D'ENERGIE NE SUFFIT PAS SUR CE REPERTOIRE.
///
/// Le flux spectral ne compte que ce qui <b>monte en energie</b>. Il voit tres bien une
/// frappe seche sur un enregistrement propre, et tres mal une frappe etouffee sous un
/// sample sature — ce qui est precisement le repertoire ici. Sur quatre morceaux du crate,
/// il ne place que 22 a 40 % des intervalles entre frappes sur un temps entier, la ou un
/// morceau plus net en donne 71 : le detecteur n'est pas casse, il est aveugle a un certain
/// type d'attaque.
///
/// CE QUE LA PHASE AJOUTE.
///
/// Une note qui commence ne change pas seulement d'amplitude : elle repart d'une phase
/// arbitraire. Entre deux fenetres d'un son stable, chaque raie avance d'un incrément de
/// phase constant, dicte par sa frequence ; une attaque rompt cette regularite meme quand
/// l'energie bouge peu.
///
/// On predit donc chaque bin a partir des deux fenetres precedentes — meme amplitude, meme
/// avance de phase — et l'on mesure de combien la realite s'en ecarte. Cette distance
/// reagit aux deux causes a la fois : le saut d'energie que voyait le flux, et le saut de
/// phase qu'il ne voyait pas.
///
/// C'est la methode de Bello et Sandler, dite « complex domain », publiee en 2004 et
/// devenue la reference pour les attaques molles.
/// </summary>
public sealed class ComplexFlux
{
    private readonly float[] _magPrec;      // magnitude de la fenetre precedente
    private readonly float[] _phasePrec;    // sa phase
    private readonly float[] _phasePrec2;   // et celle d'avant, pour l'avance
    private bool _amorce;

    /// <summary>
    /// Moyenne recente, pour rendre un rapport plutot qu'une valeur absolue.
    ///
    /// La montee des bandes, a laquelle ce signal doit s'ajouter, est un rapport contre un
    /// masque : sans dimension, autour de 1 au repos. Une somme d'ecarts en unites de
    /// spectre ne s'y additionne pas — il faut d'abord la ramener a la meme echelle, sinon
    /// le poids du melange dependrait du volume du disque.
    /// </summary>
    private float _moyenne;
    private const float Inertie = 0.02f;

    /// <summary>Le rapport de la derniere fenetre a la moyenne recente, 0 au repos.</summary>
    public float Rapport { get; private set; }

    /// <summary>La derniere somme brute, pour le diagnostic.</summary>
    public float DernierTotal { get; private set; }

    public ComplexFlux(int bins)
    {
        _magPrec = new float[bins];
        _phasePrec = new float[bins];
        _phasePrec2 = new float[bins];
    }

    /// <summary>
    /// Une fenetre, apres transformee. Rend la somme des ecarts a la prediction sur les
    /// <paramref name="jusqua"/> premiers bins.
    /// </summary>
    public float Feed(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int jusqua)
    {
        var total = 0f;

        for (var i = 0; i < jusqua; i++)
        {
            var mag = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
            var phase = MathF.Atan2(im[i], re[i]);

            if (_amorce)
            {
                // La phase attendue : celle d'avant, plus l'avance observee entre les deux
                // fenetres precedentes. Ramenee dans [-pi, pi], sans quoi un tour complet
                // passerait pour un ecart enorme.
                var attendue = Enroule(2f * _phasePrec[i] - _phasePrec2[i]);

                // Distance entre le point predit et le point reel, dans le plan complexe.
                // La forme developpee evite deux sinus et deux cosinus par bin.
                var d = MathF.Sqrt(
                    mag * mag + _magPrec[i] * _magPrec[i]
                    - 2f * mag * _magPrec[i] * MathF.Cos(phase - attendue));

                total += d;
            }

            _phasePrec2[i] = _phasePrec[i];
            _phasePrec[i] = phase;
            _magPrec[i] = mag;
        }

        _amorce = true;

        DernierTotal = total;
        _moyenne += (total - _moyenne) * Inertie;
        Rapport = _moyenne > 1e-6f ? MathF.Max(0f, total / _moyenne - 1f) : 0f;

        return total;
    }

    /// <summary>Ramene un angle dans [-pi, pi].</summary>
    private static float Enroule(float a)
    {
        const float TwoPi = MathF.PI * 2f;
        a = (a + MathF.PI) % TwoPi;
        if (a < 0) a += TwoPi;
        return a - MathF.PI;
    }

    public void Reset()
    {
        Array.Clear(_magPrec);
        Array.Clear(_phasePrec);
        Array.Clear(_phasePrec2);
        _amorce = false;
        _moyenne = 0f;
        Rapport = 0f;
    }
}
