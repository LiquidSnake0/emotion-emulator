using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Ce que le systeme sait des faces posees en ce moment. <b>En memoire vive, et rien de
/// plus.</b>
///
/// CE QUI EST GARDE, ET CE QUI EST JETE.
///
/// Une face sur une platine est connue : tant qu'elle tourne, ou qu'on repasse l'aiguille
/// pour caler, tout ce qu'on apprend d'elle s'accumule. Une face rangee est oubliee — la
/// connaissance part avec le disque.
///
/// Une premiere version ecrivait ces portraits sur le disque dur, un fichier par face. La
/// mesure a tranche : le cout median d'une image passait de 2,0 a 3,2 ms et le pire cas de
/// 17 a 34 ms, au-dessus du pas de 21 ms. Retrouver un disque la semaine prochaine ne vaut
/// pas d'alourdir la soiree en cours.
///
/// LE SEUL TRANSFERT QUI COMPTE : DU CASQUE AUX ENCEINTES.
///
/// C'est au casque qu'on apprend le plus, parce que c'est la que l'aiguille repasse.
/// Quand la face calee devient celle qui joue, sa connaissance <b>la suit</b> : le master
/// reprend un disque deja decrit au lieu de tout redecouvrir au moment ou il en a le moins
/// le temps. C'est le seul instant ou quoi que ce soit est copie — une transition est un
/// geste, pas une boucle, et rien de tout cela ne tourne pendant l'analyse.
/// </summary>
public sealed class TrackMemory
{
    private readonly ILogger<TrackMemory> _log;

    /// <summary>Ce qui joue et ce qui se prepare. Deux entrees, jamais davantage.</summary>
    private (string Id, TrackKnowledge Knowledge) _master;
    private (string Id, TrackKnowledge Knowledge) _cue;

    public TrackMemory(ILogger<TrackMemory> log) => _log = log;

    /// <summary>
    /// La face calee devient celle qui joue. Ce qu'on a appris au casque part avec elle ;
    /// ce qu'on savait de la face precedente est oublie, puisqu'elle est rangee.
    /// </summary>
    public void Handover(TrackContext playing, ILearnsTracks master, ILearnsTracks? cue)
    {
        var id = Identify(playing);

        // Ce que le casque avait appris de cette face, s'il s'agit bien de la meme.
        var carried = _cue.Id == id && cue is not null
            ? cue.Park(id, _cue.Knowledge)
            : TrackKnowledge.Empty(id);

        _master = (id, carried);
        master.Resume(carried);

        // La face n'est plus au casque : le casque repart de rien.
        _cue = default;
        cue?.Resume(TrackKnowledge.Empty(""));

        _log.LogInformation(
            carried.Any
                ? "{Track} passe au master avec {Seconds:F0} s de casque, tempo {Bpm:F1}"
                : "{Track} passe au master sans preparation",
            id, carried.SecondsHeard, carried.Bpm);
    }

    /// <summary>
    /// Une face est calee au casque. Si c'est la meme qu'avant — l'aiguille repasse pour
    /// verifier le tempo — on ne touche a rien : ce passage vient s'ajouter aux precedents.
    /// </summary>
    public void Cue(TrackContext track, ILearnsTracks cue)
    {
        var id = Identify(track);
        if (_cue.Id == id) return;

        _cue = (id, TrackKnowledge.Empty(id));
        cue.Resume(_cue.Knowledge);
    }

    /// <summary>Une face est abandonnee ou rangee : on oublie ce qu'on savait d'elle.</summary>
    public void Forget(bool cue, ILearnsTracks? learner)
    {
        if (cue) _cue = default; else _master = default;
        learner?.Resume(TrackKnowledge.Empty(""));
    }

    /// <summary>Une face est posee directement au master, sans passer par le casque.</summary>
    public void Play(TrackContext track, ILearnsTracks master)
    {
        var id = Identify(track);
        _master = (id, TrackKnowledge.Empty(id));
        master.Resume(_master.Knowledge);
    }

    /// <summary>Ce qui joue en ce moment, ou une chaine vide.</summary>
    public string Playing => _master.Id ?? "";

    /// <summary>Ce qui est cale en ce moment, ou une chaine vide.</summary>
    public string Cued => _cue.Id ?? "";

    /// <summary>
    /// Ce qui identifie une face. Le titre, faute de mieux : c'est la seule chose qu'une
    /// fiche porte toujours.
    /// </summary>
    private static string Identify(TrackContext track) =>
        string.IsNullOrWhiteSpace(track.Title) ? "sans-titre" : track.Title.Trim();
}
