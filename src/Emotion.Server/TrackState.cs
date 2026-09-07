using Emotion.Signal;

namespace Emotion.Server;

/// <summary>
/// Le morceau en cours, partage par le hub et le worker. Singleton : il n'y a qu'une
/// seule paire de platines.
///
/// Volatile plutot qu'un verrou : une reference d'objet s'ecrit d'un bloc, et le seul
/// risque etait qu'un thread lise une valeur perimee depuis son cache.
/// </summary>
public sealed class TrackState
{
    private volatile TrackContext _current = TrackContext.Silence;

    public TrackContext Current
    {
        get => _current;
        set => _current = value;
    }
}
