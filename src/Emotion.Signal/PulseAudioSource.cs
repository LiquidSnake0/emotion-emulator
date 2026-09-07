using System.Diagnostics;

namespace Emotion.Signal;

/// <summary>
/// Le vrai son, capte par PulseAudio.
///
/// Le meme adaptateur sert aux deux situations, seul le nom du peripherique change :
///
///   <c>...analog-stereo.monitor</c>  ce qui sort des haut-parleurs, pour essayer sans
///                                    materiel : on lance un morceau et le visuel suit.
///   <c>alsa_input...analog-stereo</c> l'entree ligne, le jour ou la table est branchee.
///
/// On passe par <c>parec</c> en sous-processus plutot que par une liaison native : le
/// binaire est present sur toute machine PulseAudio ou PipeWire, il rend du PCM brut sur
/// sa sortie standard, et cela evite une dependance native a compiler par plateforme.
/// Le cout est un processus fils, ce qui est negligeable au regard de ce que ca economise.
/// </summary>
public sealed class PulseAudioSource : IAudioSource
{
    private const int SampleRate = 48_000;

    private readonly string? _device;
    private readonly SpectrumAnalyzer _analyzer;

    /// <param name="device">
    /// Nom du peripherique PulseAudio. Nul signifie la source par defaut.
    /// <c>pactl list short sources</c> les enumere.
    /// </param>
    public PulseAudioSource(string? device = null)
    {
        _device = string.IsNullOrWhiteSpace(device) ? null : device;
        _analyzer = new SpectrumAnalyzer(SampleRate);
    }

    public string Name => _device is null ? "entree par defaut" : _device;

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Mono : le visuel n'a que faire de la stereo, et cela divise par deux le
        // volume de donnees a traiter soixante fois par seconde.
        var args = $"--format=s16le --rate={SampleRate} --channels=1 --latency-msec=20";
        if (_device is not null) args += $" --device={_device}";

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("parec", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        if (!proc.Start())
            throw new InvalidOperationException("impossible de lancer parec");

        try
        {
            var stream = proc.StandardOutput.BaseStream;
            var bytes = new byte[SpectrumAnalyzer.Window * 2];   // s16le
            var window = new float[SpectrumAnalyzer.Window];
            var start = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                // ReadExactly plutot qu'un Read : une fenetre partielle produirait un
                // spectre faux, avec des attaques inventees a chaque bord.
                var read = await FillAsync(stream, bytes, ct);
                if (!read) yield break;

                for (var i = 0; i < window.Length; i++)
                {
                    var v = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                    window[i] = v / 32768f;
                }

                var t = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                yield return _analyzer.Analyze(window, t);
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static async Task<bool> FillAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            int n;
            try { n = await s.ReadAsync(buffer.AsMemory(off), ct); }
            catch (OperationCanceledException) { return false; }
            if (n == 0) return false;      // parec s'est arrete
            off += n;
        }
        return true;
    }
}
