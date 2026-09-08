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
/// accroche donc le tempo et le profil de hauteurs du disque a venir.
///
/// A mi-fondu, il passe le relais : le master <b>reprend</b> ce tempo comme point de
/// depart, au lieu de repartir de rien pendant le passage le plus visible du set — un
/// analyseur met une a deux secondes a accrocher.
///
/// Mais ce n'est qu'un point de depart, et la nuance est essentielle. <b>Le master a
/// bien a decouvrir</b>, pour deux raisons : le pitch a bouge pendant le beatmatch,
/// c'est meme le but du geste, et l'EQ de la table modifie le spectre entre le casque
/// et la sortie. Ce qui joue en salle n'est donc jamais tout a fait ce que le cue a
/// entendu. Le relais amorce, il ne verrouille pas : le master continue de suivre la
/// continuite du son qui sort reellement, et sa propre mesure remplace l'amorce en
/// quelques mesures.
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
    /// A quel niveau de fondu le relais se fait. A mi-chemin : plus tot, le morceau qui
    /// arrive ne domine pas encore et le master se calerait sur ce qui va disparaitre ;
    /// plus tard, on aurait laisse passer le moment ou l'aide sert.
    /// </summary>
    private const float HandoverAt = 0.5f;

    private bool _handedOver;

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

    /// <summary>
    /// Les deux faces, pour que chacune accumule sa propre connaissance.
    ///
    /// LA FACE CALEE APPREND AUTANT QUE CELLE QUI JOUE, ET SOUVENT PLUS.
    ///
    /// C'est au casque que l'aiguille repasse : caler une face veut dire revenir au debut
    /// plusieurs fois pour verifier le tempo. Chacun de ces passages est une ecoute de plus
    /// du meme extrait, et leur cumul depasse largement une seule ecoute continue. Ne faire
    /// apprendre que le master jetterait exactement la matiere la plus abondante, et
    /// obligerait a tout redecouvrir au moment de la transition — c'est-a-dire au seul
    /// moment ou l'on n'a pas le temps.
    /// </summary>
    public IAudioSource Master => _master;
    public IAudioSource Cue => _cue;

    /// <summary>
    /// Remet la mesure a zero : nouveau disque au casque, et un nouveau relais a venir.
    /// </summary>
    public void ResetBlend()
    {
        _blend.Reset();
        _handedOver = false;
    }

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

                // Le passage de relais, une seule fois par transition.
                if (!_handedOver && blend >= HandoverAt)
                {
                    var cue = CueFrame;
                    if (cue.Bpm is { } bpm && _master is PulseAudioSource p)
                    {
                        p.AdoptTempo(bpm, frame.T);
                        _handedOver = true;
                    }
                }

                // PENDANT LE FONDU, LE MASTER SUIT MAIS N'APPREND PLUS.
                //
                // Les deux disques sonnent ensemble : ce qu'il entend est une somme qui
                // n'existe dans aucun des deux. Former un portrait la-dessus ecraserait
                // celui que le casque vient de transmettre, qui lui est propre et deja
                // constitue. Le rendu ne s'interrompt pas pour autant — seule la formation
                // du portrait attend que le fader soit arrive au bout.
                if (_master is PulseAudioSource maitre) maitre.Fondu = blend;

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
