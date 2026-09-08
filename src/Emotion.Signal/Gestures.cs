namespace Emotion.Signal;

/// <summary>
/// Les gestes du DJ, ramenes a des <b>etats</b> plutot qu'a des grandeurs.
///
/// POURQUOI CETTE COUCHE EXISTE. Selim la formule par un exemple de tempo : passer de 93 a
/// 94 BPM ne s'entend pas, donc rien ne doit bouger a l'ecran pour si peu. La regle
/// generale qu'il decrit est plus large — <i>« un intervalle pour chaque changement de
/// vitesse, filtre applique, basse mutee »</i> — et elle ne porte pas sur des amplitudes
/// mais sur des <b>basculements</b>.
///
/// La nuance decide de tout. Quantifier une amplitude serait une faute : l'ouverture d'un
/// filtre ou un niveau de basse sont deja lisses par des ressorts au rendu, et les
/// decouper en marches remplacerait un mouvement continu par des sauts. Ce qui flotte, ce
/// n'est pas la valeur, c'est la <b>reponse a une question fermee</b> : le filtre est-il
/// ferme, la basse est-elle coupee. Sans hysteresis, ces reponses changent plusieurs fois
/// par seconde quand la grandeur traine autour de son seuil, et le motif clignote.
///
/// Chaque etat a donc deux seuils : un pour entrer, un autre pour sortir. Entre les deux,
/// rien ne change — c'est precisement l'intervalle que Selim demande.
/// </summary>
/// <param name="FilterClosed">Le passe-bas est ferme : le son a perdu ses aigus.</param>
/// <param name="BassCut">Le registre grave est retire, au fader ou a l'EQ.</param>
/// <param name="Dense">Il se passe beaucoup de choses, par opposition a un passage aere.</param>
public readonly record struct Gestures(
    bool FilterClosed,
    bool BassCut,
    bool Dense)
{
    public static Gestures None => new(false, false, false);
}

/// <summary>
/// Transforme les grandeurs continues en etats stables.
/// </summary>
public sealed class GestureTracker
{
    // Deux seuils par etat : on entre bas, on sort haut. L'ecart entre les deux est
    // l'intervalle mort — assez large pour absorber le flottement ordinaire d'un geste
    // tenu, assez etroit pour qu'un vrai mouvement bascule sans retard percu.
    private const float FilterEnter = 0.55f, FilterLeave = 0.72f;
    private const float BassEnter = 0.12f, BassLeave = 0.22f;
    private const float DenseEnter = 0.55f, DenseLeave = 0.42f;

    /// <summary>
    /// Fenetres pendant lesquelles la condition doit tenir avant que l'etat ne change.
    ///
    /// L'hysteresis seule ne suffit pas : un geste rapide traverse les deux seuils d'un
    /// coup. Exiger que la condition dure supprime les basculements dus a une seule
    /// fenetre aberrante, au prix de 60 ms de retard sur un geste — invisible a cote du
    /// clignotement qu'on evite.
    /// </summary>
    private const int Hold = 3;

    private bool _filter, _bass, _dense;
    private int _filterFor, _bassFor, _denseFor;

    public Gestures Current { get; private set; } = Gestures.None;

    /// <param name="openness">ouverture du filtre, 0 ferme, 1 grand ouvert.</param>
    /// <param name="bass">energie du registre grave, normalisee.</param>
    /// <param name="density">evenements par seconde, normalisee.</param>
    public Gestures Feed(float openness, float bass, float density)
    {
        _filter = Settle(_filter, openness < FilterEnter, openness > FilterLeave, ref _filterFor);
        _bass = Settle(_bass, bass < BassEnter, bass > BassLeave, ref _bassFor);
        _dense = Settle(_dense, density > DenseEnter, density < DenseLeave, ref _denseFor);

        Current = new Gestures(_filter, _bass, _dense);
        return Current;
    }

    public void Reset()
    {
        _filter = _bass = _dense = false;
        _filterFor = _bassFor = _denseFor = 0;
        Current = Gestures.None;
    }

    private static bool Settle(bool state, bool enter, bool leave, ref int held)
    {
        var wants = state ? !leave : enter;
        if (wants == state) { held = 0; return state; }

        return ++held >= Hold ? !state : state;
    }
}
