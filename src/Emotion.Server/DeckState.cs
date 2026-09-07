using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// L'etat des platines, partage par le hub et les commandes. Singleton : il n'y a
/// qu'une paire de platines.
///
/// Les mutations passent par <see cref="Apply"/> sous verrou. Elles sont rares — une
/// poignee par set — mais elles se lisent depuis plusieurs connexions a la fois, et
/// deux commandes simultanees ne doivent pas se marcher dessus : un <c>Cue</c> qui
/// croiserait un <c>Take</c> pourrait faire jouer une face qui vient d'etre abandonnee.
/// </summary>
public sealed class DeckState
{
    private readonly Lock _gate = new();
    private Deck _deck = Deck.Empty;

    public Deck Current
    {
        get { lock (_gate) return _deck; }
    }

    /// <summary>Applique une transition et rend le nouvel etat.</summary>
    public Deck Apply(Func<Deck, Deck> change)
    {
        lock (_gate)
        {
            _deck = change(_deck);
            return _deck;
        }
    }
}
