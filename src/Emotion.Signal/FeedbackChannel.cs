using System.IO.MemoryMappedFiles;

namespace Emotion.Signal;

/// <summary>
/// Le canal de retour, en memoire partagee, sans verrou.
///
/// UN COMPTEUR DE VERSION PLUTOT QU'UN VERROU, ET LA RAISON TIENT AU SENS DU FLUX.
///
/// L'ecrivain est l'unite de rendu ; le lecteur est l'analyse, qui tourne a cadence fixe et
/// ne doit jamais attendre. Un verrou ferait exactement ce qu'on interdit ici : bloquer le
/// fil d'analyse parce qu'un autre processus tient la donnee. On emploie donc un compteur
/// de version — l'ecrivain l'incremente avant et apres son ecriture, le lecteur relit tant
/// qu'il a bouge ou qu'il est impair. L'ecrivain n'attend jamais, le lecteur non plus, et
/// aucun des deux ne peut voir une structure a moitie ecrite.
///
/// C'est la meme famille de solution que l'anneau de l'aller, pour la meme raison : entre
/// deux processus qui tiennent une cadence, on separe plutot qu'on arbitre.
/// </summary>
public sealed unsafe class FeedbackChannel : IDisposable
{
    /// <summary>Version sur 8 octets, puis la structure. Une page suffit largement.</summary>
    private const int VersionOffset = 0;
    private const int DataOffset = 64;
    private const int TotalSize = 128;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    public string Path { get; }

    public FeedbackChannel(string path = "/dev/shm/emotion-feedback")
    {
        Path = path;

        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                FileShare.ReadWrite);
        if (fs.Length < TotalSize) fs.SetLength(TotalSize);

        _file = MemoryMappedFile.CreateFromFile(fs, null, TotalSize,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        _view = _file.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
    }

    /// <summary>
    /// Publie un retour. Appele par l'unite de rendu, une fois par image affichee.
    ///
    /// L'ordre des trois etapes est la seule chose qui protege le lecteur : version impaire,
    /// puis donnee, puis version paire. Un lecteur qui tombe au milieu voit un compteur
    /// impair et recommence.
    /// </summary>
    public void Publish(in RenderFeedback feedback)
    {
        var v = *(long*)(_base + VersionOffset);

        Volatile.Write(ref *(long*)(_base + VersionOffset), v + 1);   // impair : ecriture en cours
        Thread.MemoryBarrier();

        *(RenderFeedback*)(_base + DataOffset) = feedback;

        Thread.MemoryBarrier();
        Volatile.Write(ref *(long*)(_base + VersionOffset), v + 2);   // pair : lisible
    }

    /// <summary>
    /// Lit le dernier retour publie. Rend faux si rien n'a jamais ete ecrit, ou si
    /// l'ecrivain a change la donnee pendant la lecture — auquel cas la suivante ira.
    /// </summary>
    public bool TryRead(out RenderFeedback feedback)
    {
        for (var essai = 0; essai < 4; essai++)
        {
            var avant = Volatile.Read(ref *(long*)(_base + VersionOffset));
            if (avant == 0 || (avant & 1) != 0) break;     // jamais ecrit, ou en cours

            feedback = *(RenderFeedback*)(_base + DataOffset);

            Thread.MemoryBarrier();
            var apres = Volatile.Read(ref *(long*)(_base + VersionOffset));

            if (avant == apres && feedback.Magic == RenderFeedback.MagicValue)
                return true;
        }

        feedback = default;
        return false;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
