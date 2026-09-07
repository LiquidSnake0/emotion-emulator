namespace Emotion.Signal;

/// <summary>
/// Deux entrees : le master, qui sort en salle, et le cue, ce que Selim ecoute au
/// casque pendant qu'il cale son prochain disque.
///
/// La seconde entree est ce qui permet au visuel de <b>suivre une transition au lieu de
/// l'annoncer</b>. Avec le master seul, on ne peut que basculer sur commande ; avec les
/// deux, on mesure combien du prepare est deja passe dans le melange, et la projection
/// glisse au rythme du fader.
///
/// C'est aussi ce qui prepare le systeme : pendant tout le beatmatch, l'analyseur du cue
/// tourne deja sur le disque a venir. Ses seuils sont donc cales et son tempo verrouille
/// avant meme que le public en entende la premiere note.
///
/// <b>Le cue est un analyseur de plein droit, pas un capteur.</b> Il a son propre
/// detecteur d'attaques, son propre estimateur de tempo, sa propre analyse harmonique —
/// tout ce que possede le master. Pendant les huit ou seize mesures du beatmatch, il
/// verrouille donc le tempo et le profil de hauteurs du disque a venir, si bien qu'au
/// moment ou le fondu se fait, <b>le master n'a plus rien a decouvrir</b>. Un analyseur
/// met une a deux secondes a accrocher un tempo : sur une transition, ces deux secondes
/// tomberaient en plein milieu du passage le plus visible du set.
///
/// Le master mene la cadence et s'abonne au cue : c'est lui qui emet les images, en y
/// joignant la part du prepare deja passee dans le melange. Si le cue s'arrete — casque
/// debranche, table eteinte — le master continue seul avec un fondu mesure a zero.
/// </summary>
public sealed class DualAudioSource : IAudioSource
{
    private readonly IAudioSource _master;
    private readonly IAudioSource _cue;
    private readonly BlendEstimator _blend = new();

    private volatile float[] _lastCueBands = new float[VisualFrame.BandCount];
    private VisualFrame _cueFrame;
    private readonly Lock _cueGate = new();

    /// <summary>
    /// La derniere analyse du cue : tempo, harmonie, registres du disque en preparation.
    /// Elle alimente le bandeau du DJ — jamais la projection, qui ne doit rien laisser
    /// voir du beatmatch.
    /// </summary>
    public VisualFrame CueFrame
    {
        get { lock (_cueGate) return _cueFrame; }
        private set { lock (_cueGate) _cueFrame = value; }
    }

    public DualAudioSource(IAudioSource master, IAudioSource cue)
    {
        _master = master;
        _cue = cue;
    }

    public string Name => $"{_master.Name} + cue {_cue.Name}";

    /// <summary>Remet la mesure a zero : nouveau disque au casque.</summary>
    public void ResetBlend() => _blend.Reset();

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Le cue tourne dans sa propre boucle et depose son dernier profil de bandes.
        // On ne synchronise pas les deux flux a l'echantillon pres : la correlation
        // porte sur une seconde et demie, un decalage de quelques dizaines de
        // millisecondes entre les deux cartes n'a aucun effet sur elle.
        var cueLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in _cue.ReadAsync(stop.Token))
                {
                    _lastCueBands = f.Bands;
                    CueFrame = f;
                }
            }
            catch (OperationCanceledException) { }
        }, stop.Token);

        try
        {
            await foreach (var frame in _master.ReadAsync(stop.Token))
            {
                var blend = _blend.Feed(frame.Bands, _lastCueBands);
                yield return frame with { Blend = blend };
            }
        }
        finally
        {
            stop.Cancel();
            try { await cueLoop; } catch { }
        }
    }
}
