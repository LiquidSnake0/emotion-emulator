namespace Emotion.Signal;

/// <summary>
/// D'ou vient le signal. Une seule implementation tourne a la fois, choisie par la
/// configuration.
///
/// Aujourd'hui <see cref="MockAudioSource"/>, faute de table branchee. Demain une
/// source d'entree ligne. Le hub, le contrat <see cref="VisualFrame"/> et le renderer
/// ne changent pas d'une ligne : c'est tout l'interet de passer par ici.
/// </summary>
public interface IAudioSource
{
    /// <summary>Nom affiche dans le bandeau du renderer, pour savoir ce qu'on ecoute.</summary>
    string Name { get; }

    /// <summary>
    /// Emet les images du signal jusqu'a annulation. Un flux tire, pas un evenement :
    /// c'est le consommateur qui cadence, et un renderer lent ne noie pas le hub.
    /// </summary>
    IAsyncEnumerable<VisualFrame> ReadAsync(CancellationToken ct);
}
