using System.Diagnostics;

namespace Emotion.Signal;

/// <summary>
/// Ou passe le temps dans une image d'analyse.
///
/// POURQUOI MESURER PAR ETAGE ET NON GLOBALEMENT.
///
/// Le cout total d'une image se mesure d'une ligne, et il ne dit rien : quand il monte, on
/// ne sait pas lequel des douze etages a bouge. Le projet a deja paye cette ignorance —
/// l'apprentissage des timbres bloquait le fil d'analyse une demi-seconde toutes les 1,4 s,
/// et il a fallu chronometrer chaque partie pour le trouver. Un total ne se debogue pas.
///
/// DEUX RETARDS DIFFERENTS, ET IL FAUT LES DEUX.
///
/// Le <b>temps de calcul</b> est ce que le processeur passe a travailler : il se reduit en
/// ecrivant mieux, et il ne compte que s'il approche du pas de 21 ms.
///
/// Le <b>retard algorithmique</b> est celui qu'aucune optimisation ne retire : il faut avoir
/// entendu une fenetre entiere avant de la transformer, et avoir vu la fenetre suivante
/// avant de dire qu'on etait sur un sommet. C'est lui qui domine, et de loin — d'ou la
/// solution retenue ailleurs, qui n'est pas d'aller plus vite mais de <b>ne plus attendre</b>
/// grace a l'horloge a verrouillage de phase.
///
/// COUT QUAND C'EST ETEINT : nul. Un booleen teste, aucun appel d'horloge.
/// </summary>
public sealed class Etapes
{
    /// <summary>Les etages, dans l'ordre ou le signal les traverse.</summary>
    public static readonly string[] Noms =
    [
        "harmonie", "transitoire", "fenetre + FFT", "separation H/P", "registres",
        "timbres (suivi)", "flux + attaque", "12 bandes", "tempo",
        "kick/clap/hat", "couleur", "structure",
    ];

    private readonly double[] _total = new double[Noms.Length];
    private readonly double[] _pire = new double[Noms.Length];
    private long _debut;
    private int _images;

    /// <summary>La mesure tourne-t-elle. Eteinte, elle ne coute rien.</summary>
    public bool Actif { get; set; }

    public int Images => _images;

    /// <summary>Marque le debut d'un etage.</summary>
    public void Debut()
    {
        if (Actif) _debut = Stopwatch.GetTimestamp();
    }

    /// <summary>Ferme l'etage <paramref name="rang"/> et repart pour le suivant.</summary>
    public void Fin(int rang)
    {
        if (!Actif) return;

        var maintenant = Stopwatch.GetTimestamp();
        var us = (maintenant - _debut) * 1_000_000.0 / Stopwatch.Frequency;
        _total[rang] += us;
        if (us > _pire[rang]) _pire[rang] = us;
        _debut = maintenant;
    }

    /// <summary>Une image de plus est passee.</summary>
    public void Image()
    {
        if (Actif) _images++;
    }

    /// <summary>Cout moyen d'un etage, en microsecondes par image.</summary>
    public double Moyenne(int rang) => _images == 0 ? 0 : _total[rang] / _images;

    /// <summary>Pire passage d'un etage, en microsecondes.</summary>
    public double Pire(int rang) => _pire[rang];

    /// <summary>Somme des etages, en microsecondes par image.</summary>
    public double Totale()
    {
        double t = 0;
        for (var i = 0; i < Noms.Length; i++) t += Moyenne(i);
        return t;
    }

    public void Reset()
    {
        Array.Clear(_total);
        Array.Clear(_pire);
        _images = 0;
    }
}
