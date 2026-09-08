using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Range en continu ce que le systeme apprend, sans attendre de geste.
///
/// POURQUOI UN SERVICE ET NON UN APPEL DANS LES COMMANDES.
///
/// L'essentiel de l'apprentissage n'a pas lieu au moment ou l'on change de disque : il a
/// lieu <b>pendant qu'on prepare</b>. Caler une face au casque veut dire remettre l'aiguille
/// au debut plusieurs fois pour verifier le tempo, et chacun de ces passages est une ecoute
/// de plus du meme passage. Cumulees sur une soiree, ces reprises depassent de loin ce
/// qu'une seule ecoute continue aurait donne.
///
/// Ranger seulement sur commande jetterait tout cela : entre deux transitions il peut
/// s'ecouler dix minutes, et rien ne garantit qu'une transition arrive avant la fin du set
/// ou avant qu'un cable ne bouge. Le rangement est donc periodique et autonome.
///
/// LE RYTHME. Dix secondes : assez court pour qu'une coupure ne coute presque rien, assez
/// long pour que l'ecriture — quelques centaines d'octets — ne pese sur rien. Elle se fait
/// hors du fil d'analyse, comme tout ce qui n'a pas a tenir la cadence.
/// </summary>
public sealed class MemoryKeeper : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private readonly TrackMemory _memory;
    private readonly IAudioSource _source;
    private readonly DeckState _deck;
    private readonly ILogger<MemoryKeeper> _log;

    public MemoryKeeper(TrackMemory memory, IAudioSource source, DeckState deck,
                        ILogger<MemoryKeeper> log)
    {
        _memory = memory;
        _source = source;
        _deck = deck;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var master = Learner(_source);
        var cue = _source is DualAudioSource dual ? Learner(dual.Cue) : null;

        if (master is null && cue is null)
        {
            _log.LogInformation("La source n'apprend pas : rien a ranger.");
            return;
        }

        using var timer = new PeriodicTimer(Period);

        try
        {
            do Tick(master, cue);
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }

        // Un dernier rangement en partant : ce qui a ete appris depuis le precedent ne
        // doit pas disparaitre parce qu'on arrete le service proprement.
        Park(master, cue);
    }

    /// <summary>
    /// Suit les deux faces et range ce qu'elles ont appris.
    ///
    /// La face calee est reprise a chaque passage : caler veut dire remettre l'aiguille au
    /// debut, souvent plusieurs fois, et chacun de ces retours doit venir s'ajouter au lieu
    /// de recommencer. <see cref="TrackMemory.Switch"/> ne fait rien quand le disque n'a pas
    /// change, ce qui rend cet appel repete sans effet de bord.
    /// </summary>
    private void Tick(ILearnsTracks? master, ILearnsTracks? cue)
    {
        var deck = _deck.Current;

        if (master is not null) _memory.Switch(TrackMemory.MasterLane, deck.Playing, master);
        if (cue is not null && deck.Cued is { } cued)
            _memory.Switch(TrackMemory.CueLane, cued, cue);

        Park(master, cue);
    }

    private void Park(ILearnsTracks? master, ILearnsTracks? cue)
    {
        if (master is not null) _memory.Park(TrackMemory.MasterLane, master);
        if (cue is not null) _memory.Park(TrackMemory.CueLane, cue);
    }

    private static ILearnsTracks? Learner(IAudioSource source) => source switch
    {
        ILearnsTracks l => l,
        DualAudioSource d => d.Master as ILearnsTracks,
        _ => null,
    };
}
