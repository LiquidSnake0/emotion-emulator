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
    /// L'analyseur de cette source, quand elle en a un.
    ///
    /// Il n'est pas la pour etre pilote : rien ne doit ecrire au travers. Il sert a LIRE ce
    /// que la session a appris — les six profils spectraux, que l'extraction hors ligne a
    /// besoin de prendre a CETTE session-ci et non a une autre, les rangs n'etant pas
    /// stables d'un apprentissage au suivant.
    ///
    /// Nul par defaut : une source fabriquee n'analyse rien.
    /// </summary>
    SpectrumAnalyzer? Analyzer => null;

    /// <summary>
    /// Emet les images du signal jusqu'a annulation. Un flux tire, pas un evenement :
    /// c'est le consommateur qui cadence, et un renderer lent ne noie pas le hub.
    /// </summary>
    IAsyncEnumerable<VisualFrame> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Un autre disque commence : tout ce qui decrivait le precedent est a jeter.
    ///
    /// C'est la seule chose que la base sait et que le signal ne dira pas a temps. Une
    /// analyse qui l'ignore met des dizaines de secondes a admettre le changement, son
    /// oubli etant lent par construction — et elle annoncerait pendant ce temps qu'elle
    /// « connait » un disque qui ne joue plus.
    ///
    /// Implementation vide par defaut : une source fabriquee n'a rien a reapprendre.
    /// </summary>
    void NewTrack() { }
}
