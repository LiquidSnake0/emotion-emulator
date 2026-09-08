namespace Emotion.Signal;

/// <summary>
/// Ce qu'il faut savoir faire pour apprendre d'un disque a l'autre.
///
/// Une interface a part et non deux methodes ajoutees a <see cref="IAudioSource"/> : une
/// source qui lit un fichier de test n'a aucune raison de savoir ranger une connaissance,
/// et l'obliger a implementer ces methodes pour ne rien en faire alourdirait tout le
/// projet pour un seul usage.
/// </summary>
public interface ILearnsTracks
{
    /// <summary>Reprend ce qu'on savait de ce disque.</summary>
    void Resume(in TrackKnowledge knowledge);

    /// <summary>Rend ce qu'on sait maintenant, pour rangement.</summary>
    TrackKnowledge Park(string id, in TrackKnowledge previous);
}
