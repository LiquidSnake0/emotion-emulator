namespace Emotion.Signal;

/// <summary>
/// Situe l'attaque <b>a l'interieur</b> de la fenetre d'analyse.
///
/// LE PROBLEME QU'ON NE VOYAIT PAS. Une fenetre de 1024 echantillons dure 21 ms. Tout ce
/// qu'elle contient est rapporte a un seul instant, celui de la fenetre — un kick tombe au
/// premier echantillon et un kick tombe au dernier sont annonces au meme moment. La grille
/// metrique se cale donc sur une position vraie a 21 ms pres, et l'oeil decroche vers 40.
///
/// LA REPONSE DE PUCKETTE. <c>bonk~</c> travaille sur 256 echantillons avec un banc de
/// filtres plutot que sur 1024 avec une FFT : il sacrifie la resolution frequentielle,
/// dont une detection d'attaque n'a aucun besoin, pour la resolution temporelle, dont elle
/// vit. C'est le compromis de Gabor assume dans l'autre sens — celui qu'on avait deja fait
/// pour l'harmonie, sur une fenetre quatre fois <i>plus longue</i>, et jamais dans ce
/// sens-ci.
///
/// CE QU'ON EN FAIT ICI. Plutot que de raccourcir la fenetre de tout le systeme, ce qui
/// ruinerait l'analyse harmonique, on garde les 1024 echantillons et l'on <b>y regarde de
/// plus pres</b> : un passe-bas isole le registre du kick, huit sous-blocs de 2,7 ms
/// donnent son enveloppe, et la montee la plus franche donne l'instant de la frappe.
///
/// Pas de FFT : sur un seul registre, un filtre du premier ordre suffit et coute
/// quelques additions. C'est exactement pour cela que <c>bonk~</c> n'en utilise pas non
/// plus.
/// </summary>
public sealed class TransientLocator
{
    /// <summary>
    /// Sous-blocs par fenetre. Huit donnent 2,7 ms de resolution a 48 kHz — soit huit fois
    /// mieux que la fenetre, ce qui suffit largement : l'oreille ne distingue pas deux
    /// attaques separees de moins de 3 ms.
    /// </summary>
    private const int Blocks = 8;

    /// <summary>
    /// Coefficient du passe-bas, pour une coupure autour de 150 Hz a 48 kHz. On ne cherche
    /// pas un filtre propre mais un registre : le corps du kick, sans les mediums qui
    /// brouilleraient l'enveloppe.
    /// </summary>
    private const float Cutoff = 0.02f;

    private float _lp;
    private float _tail;   // energie du dernier sous-bloc de la fenetre precedente

    /// <summary>
    /// Position de l'attaque dans la fenetre, en millisecondes depuis son debut.
    /// </summary>
    public float OffsetMs { get; private set; }

    /// <summary>
    /// Nettete de la montee : le rapport d'energie entre le sous-bloc retenu et celui qui
    /// le precede. Sert a savoir si l'instant trouve veut dire quelque chose.
    /// </summary>
    public float Sharpness { get; private set; }

    public void Feed(ReadOnlySpan<float> samples, int sampleRate)
    {
        var per = samples.Length / Blocks;
        if (per == 0) return;

        Span<float> energy = stackalloc float[Blocks];

        for (var b = 0; b < Blocks; b++)
        {
            var sum = 0f;
            var from = b * per;

            for (var i = 0; i < per; i++)
            {
                _lp += (samples[from + i] - _lp) * Cutoff;
                sum += _lp * _lp;
            }

            energy[b] = sum / per;
        }

        // La montee la plus franche, chaque sous-bloc etant compare a son predecesseur —
        // le premier au dernier de la fenetre precedente, sans quoi une attaque tombant
        // pile sur la frontiere serait invisible.
        var best = 0;
        var bestRatio = 0f;

        for (var b = 0; b < Blocks; b++)
        {
            var before = b == 0 ? _tail : energy[b - 1];
            var ratio = energy[b] / (before + 1e-9f);
            if (ratio > bestRatio) { bestRatio = ratio; best = b; }
        }

        _tail = energy[Blocks - 1];
        Sharpness = bestRatio;

        // Le centre du sous-bloc retenu : on ne sait pas ou l'attaque tombe dedans, et
        // pretendre le contraire ajouterait une precision qu'on n'a pas.
        OffsetMs = (best + 0.5f) * per * 1000f / sampleRate;
    }

    public void Reset()
    {
        _lp = 0f;
        _tail = 0f;
        OffsetMs = 0f;
        Sharpness = 0f;
    }
}
