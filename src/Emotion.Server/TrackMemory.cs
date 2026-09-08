using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Fait le lien entre un disque qui commence et ce qu'on savait deja de lui.
///
/// POURQUOI CE SERVICE PLUTOT QU'UN APPEL DANS LES COMMANDES.
///
/// Deux gestes changent le disque — poser directement, ou basculer ce qui etait cale — et
/// tous deux doivent ranger ce qui vient de finir avant de reprendre ce qui commence. Ecrit
/// deux fois, cet enchainement finit par diverger : c'est deja arrive dans ce projet avec
/// le nombre de cotes du polygone, calcule a deux endroits qui ne rendaient pas la meme
/// valeur. Il vit donc ici, une fois.
///
/// L'ORDRE COMPTE, ET IL N'EST PAS INTERCHANGEABLE. On range d'abord, on reprend ensuite :
/// l'inverse ecraserait ce qu'on vient d'apprendre du disque precedent avec ce qu'on savait
/// du suivant.
/// </summary>
public sealed class TrackMemory
{
    private readonly KnowledgeStore _store;
    private readonly ILogger<TrackMemory> _log;

    /// <summary>
    /// Une entree par voie : ce qui joue et ce qui se prepare apprennent chacun de leur
    /// cote. Les melanger ferait ranger le portrait d'une face sous le nom de l'autre.
    /// </summary>
    private readonly Dictionary<string, (string Id, TrackKnowledge Knowledge)> _lanes = new();

    public TrackMemory(KnowledgeStore store, ILogger<TrackMemory> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Un disque commence. Ce qu'on a appris du precedent part sur le disque, et ce qu'on
    /// savait du nouveau revient dans l'analyse.
    ///
    /// <paramref name="learner"/> est passe plutot que resolu ici : ce service ne doit pas
    /// dependre d'une source audio particuliere, et une source de test doit pouvoir prendre
    /// sa place sans rien changer.
    /// </summary>
    public void Switch(string lane, TrackContext track, ILearnsTracks learner)
    {
        var id = Identify(track);
        if (_lanes.TryGetValue(lane, out var held) && held.Id == id) return;

        Park(lane, learner);

        var knowledge = _store.Load(id);
        _lanes[lane] = (id, knowledge);
        learner.Resume(knowledge);

        _log.LogInformation(
            knowledge.Any
                ? "{Lane} · reprise de {Track} : {Seconds:F0} s deja entendues, tempo connu {Bpm:F1}"
                : "{Lane} · premiere ecoute de {Track}",
            lane, id, knowledge.SecondsHeard, knowledge.Bpm);
    }

    /// <summary>
    /// Range ce qu'on sait du disque en cours, sans changer de disque.
    ///
    /// APPELE EN CONTINU, ET C'EST INDISPENSABLE.
    ///
    /// Ranger seulement au changement de disque perdrait tout ce qu'on apprend pendant la
    /// preparation, qui est justement le moment ou l'on apprend le plus. Caler une face au
    /// casque veut dire remettre l'aiguille au debut plusieurs fois de suite pour verifier
    /// le tempo : chaque passage est une ecoute de plus du meme passage, et cumulees, ces
    /// reprises font bien plus que les vingt-quatre secondes d'un seul essai.
    ///
    /// Rien de tout cela ne doit dependre d'un geste. Un rangement periodique attrape ces
    /// reprises comme le reste, et une coupure de courant en plein set ne coute alors que
    /// les quelques secondes ecoulees depuis le dernier.
    /// </summary>
    public void Park(string lane, ILearnsTracks learner)
    {
        if (!_lanes.TryGetValue(lane, out var held)) return;

        var updated = learner.Park(held.Id, held.Knowledge);
        _lanes[lane] = (held.Id, updated);
        _store.Save(updated);
    }

    /// <summary>
    /// Un disque est pose sans transition — au demarrage, ou pour la face qu'on prepare.
    /// Utile quand la connaissance doit commencer a s'accumuler avant que le disque ne
    /// passe au master.
    /// </summary>
    public void Follow(string lane, TrackContext track, ILearnsTracks learner) =>
        Switch(lane, track, learner);

    /// <summary>Le disque suivi sur une voie, ou une chaine vide.</summary>
    public string CurrentOn(string lane) =>
        _lanes.TryGetValue(lane, out var held) ? held.Id : "";

    /// <summary>Les deux voies, nommees une fois pour toutes.</summary>
    public const string MasterLane = "master";
    public const string CueLane = "cue";

    /// <summary>
    /// Ce qui identifie un disque d'une ecoute a l'autre.
    ///
    /// Le titre, faute de mieux : c'est la seule chose qu'une fiche porte toujours. Deux
    /// faces homonymes se melangeraient, ce qui est un defaut connu et sans consequence
    /// tant qu'un crate reste celui d'une personne.
    /// </summary>
    private static string Identify(TrackContext track) =>
        string.IsNullOrWhiteSpace(track.Title) ? "sans-titre" : track.Title.Trim();
}
