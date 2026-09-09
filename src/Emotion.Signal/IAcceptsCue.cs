namespace Emotion.Signal;

/// <summary>
/// Ce qui sait recevoir le tempo annonce par la fiche du crate.
///
/// SEPARE DE <see cref="ILearnsTracks"/>, ET LA DISTINCTION EST REELLE. Reprendre concerne
/// ce que le systeme a lui-meme appris d'un disque lors d'une ecoute precedente ; amorcer
/// concerne ce que le DJ a note dans sa base. Les deux arrivent au meme moment et ne
/// veulent pas dire la meme chose : le premier est une mesure, le second une indication.
///
/// La fiche ne verrouille jamais. Elle recentre la ponderation de l'autocorrelation, qui
/// continue de chercher — un disque pousse au fader est suivi malgre sa fiche.
/// </summary>
public interface IAcceptsCue
{
    /// <summary>Le tempo annonce par la fiche, en BPM.</summary>
    void Amorcer(float bpm);
}
