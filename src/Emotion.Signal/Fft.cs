namespace Emotion.Signal;

/// <summary>
/// Transformee de Fourier rapide, radix 2, en place.
///
/// Ecrite a la main plutot que tiree d'un paquet : la seule chose dont le projet a
/// besoin est le module du spectre d'une fenetre de 1024 points, soixante fois par
/// seconde. Une dependance de calcul scientifique pour cela couterait plus en surface
/// qu'elle ne rapporte, et le jour d'un entretien c'est le genre de code qu'on preferera
/// lire plutot qu'un appel de bibliotheque.
/// </summary>
public static class Fft
{
    /// <summary>
    /// Transforme en place. <paramref name="re"/> et <paramref name="im"/> doivent
    /// avoir la meme longueur, et cette longueur doit etre une puissance de deux.
    /// </summary>
    public static void Forward(float[] re, float[] im)
    {
        var n = re.Length;
        if (n != im.Length) throw new ArgumentException("parties reelle et imaginaire de tailles differentes");
        if ((n & (n - 1)) != 0) throw new ArgumentException("la taille doit etre une puissance de deux", nameof(re));

        // Permutation par inversion de bits : place chaque echantillon la ou les
        // papillons iront le chercher.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wr = (float)Math.Cos(ang);
            var wi = (float)Math.Sin(ang);

            for (var i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = re[i + k];
                    var ui = im[i + k];
                    var vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    var vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;

                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;

                    var nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }

    /// <summary>
    /// Fenetre de Hann. Sans elle, une note qui ne tombe pas pile sur un bin fuit sur
    /// tout le spectre et les bandes graves se remplissent de bruit d'aigu.
    /// </summary>
    public static float[] Hann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (n - 1)));
        return w;
    }
}
