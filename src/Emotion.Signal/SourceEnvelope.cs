namespace Emotion.Signal;

/// <summary>
/// Comment une source ATTAQUE et comment elle TIENT.
///
/// CE QUE LE SYSTEME NE SAVAIT PAS DIRE.
///
/// Chaque source publiait son niveau, sa hauteur et un drapeau de frappe. Rien de tout
/// cela ne distingue une corde pincee d'un souffle : l'une monte d'un coup puis meurt,
/// l'autre s'installe et ne frappe jamais. Les deux produisent le meme niveau moyen, la
/// meme hauteur, et le rendu leur donnait donc le meme mouvement.
///
/// C'est le DJ qui l'a formule : « un instrument a corde c'est une frappe suivie d'une
/// onde courte ou longue, un instrument a vent c'est en continu, il ne frappe pas ». Deux
/// grandeurs suffisent a porter cette difference.
///
///   PIQUE   a quel point la source monte d'un coup, 0 a 1.
///           Une corde pincee, une frappe : proche de 1. Un archet, un souffle : proche de 0.
///
///   TENUE   a quel point elle reste au niveau qu'elle a atteint, 0 a 1.
///           Un souffle : proche de 1. Un pizzicato : proche de 0.
///
/// LES DEUX SONT INDEPENDANTES, et c'est ce qui les rend utiles. Une note d'orgue a un
/// pique faible et une tenue forte ; un woodblock, l'inverse ; un piano, un pique fort et
/// une tenue moyenne — c'est-a-dire exactement la « frappe suivie d'une onde courte ou
/// longue » du DJ, ou la longueur de l'onde EST la tenue.
///
/// CE SONT DES DESCRIPTEURS, PAS DES EVENEMENTS, et cette distinction commande tout ce qui
/// se passe en aval. Un evenement est instantane et ne doit jamais etre lisse — une
/// impulsion lissee n'est plus une impulsion. Ces deux-ci decrivent au contraire la NATURE
/// d'une source, qui ne change pas d'une fenetre a l'autre : elles se moyennent sur
/// plusieurs secondes, se transportent comme le niveau, et s'interpolent comme lui. Le
/// drapeau de frappe, lui, reste brut.
/// </summary>
public sealed class SourceEnvelope
{
    /// <summary>
    /// Sur combien de temps la crete et la moyenne sont observees, en secondes.
    ///
    /// Une seconde et demie couvre deux temps du repertoire : assez pour qu'une note
    /// entiere y tienne — attaque, chute et silence — et assez court pour qu'un changement
    /// d'instrument se voie avant la fin de la phrase.
    /// </summary>
    public const float FenetreS = 1.5f;

    /// <summary>
    /// Temps de montee au-dela duquel on ne parle plus d'attaque, en secondes.
    ///
    /// Quarante millisecondes, soit deux fenetres d'analyse. C'est aussi le seuil ou l'oeil
    /// cesse de lier une image a un son — au-dela, ce qui monte n'est plus percu comme une
    /// frappe mais comme une arrivee, et le rendu doit le montrer autrement.
    /// </summary>
    public const float MonteeMaxS = 0.040f;

    private readonly float _frameS;
    private readonly float[] _crete;
    private readonly float[] _moyenne;
    private readonly float[] _precedent;
    private readonly float[] _pique;
    private readonly float _oubli;

    public SourceEnvelope(int sources, float frameSeconds)
    {
        _frameS = MathF.Max(1e-4f, frameSeconds);
        _crete = new float[sources];
        _moyenne = new float[sources];
        _precedent = new float[sources];
        _pique = new float[sources];

        // L'oubli est exprime en fenetres pour ne pas dependre du taux d'echantillonnage :
        // une constante en fenetres ferait glisser la mesure de huit pour cent entre 44,1
        // et 48 kHz, exactement comme le contour melodique s'y est deja fait prendre.
        _oubli = MathF.Exp(-_frameS / FenetreS);
    }

    /// <summary>Une fenetre d'analyse, pour une source.</summary>
    public void Feed(int rang, float niveau)
    {
        if ((uint)rang >= (uint)_crete.Length) return;
        niveau = Math.Clamp(niveau, 0f, 1f);

        // LA CRETE DECROIT, ELLE NE SE FIGE PAS. Un maximum brut serait fixe par le premier
        // accident venu et vaudrait pour toute la soiree — c'est un piege que ce projet a
        // deja paye ailleurs. Elle suit donc ce qui monte tout de suite, et oublie
        // lentement ce qui redescend.
        _crete[rang] = niveau > _crete[rang]
            ? niveau
            : _crete[rang] * _oubli;
        _moyenne[rang] += (niveau - _moyenne[rang]) * (1f - _oubli);

        // LE PIQUE SE MESURE SUR LA PENTE, RAPPORTEE A LA CRETE.
        //
        // Rapportee, parce qu'une source jouee fort monterait sinon plus vite qu'une source
        // jouee doucement sans etre plus percussive pour autant : on mesurerait le volume du
        // disque et non la nature de l'instrument.
        //
        // Une montee qui atteint la crete en une fenetre vaut un ; en deux fenetres, un
        // demi. Au-dela de MonteeMaxS on ne parle plus d'attaque.
        var pente = niveau - _precedent[rang];
        _precedent[rang] = niveau;
        if (pente > 0f && _crete[rang] > 1e-3f)
        {
            var parFenetre = pente / _crete[rang];
            var monte = Math.Clamp(parFenetre * (MonteeMaxS / _frameS), 0f, 1f);
            // On garde la plus franche des montees recentes plutot que leur moyenne : une
            // source qui frappe une fois par temps passe l'essentiel du temps a ne pas
            // frapper, et sa moyenne dirait « continue ».
            if (monte > _pique[rang]) _pique[rang] = monte;
        }
        _pique[rang] *= _oubli;
    }

    /// <summary>A quel point la source monte d'un coup, 0 a 1.</summary>
    public float Pique(int rang) =>
        (uint)rang < (uint)_pique.Length ? Math.Clamp(_pique[rang], 0f, 1f) : 0f;

    /// <summary>
    /// A quel point elle reste au niveau atteint, 0 a 1.
    ///
    /// C'est la moyenne rapportee a la crete — l'inverse du facteur de crete. Un souffle
    /// egal donne un ; un pizzicato, qui passe l'essentiel de son temps silencieux entre
    /// deux notes, donne peu. Aucun reglage : c'est un rapport, et il se lit tel quel.
    /// </summary>
    public float Tenue(int rang)
    {
        if ((uint)rang >= (uint)_crete.Length) return 0f;
        var c = _crete[rang];
        return c > 1e-3f ? Math.Clamp(_moyenne[rang] / c, 0f, 1f) : 0f;
    }

    /// <summary>Oublie tout : changement de disque.</summary>
    public void Reset()
    {
        Array.Clear(_crete);
        Array.Clear(_moyenne);
        Array.Clear(_precedent);
        Array.Clear(_pique);
    }
}
