namespace Emotion.Signal;

/// <summary>
/// Rejoue un enregistrement <b>au rythme reel</b>, avec les memes horodatages que la
/// capture en direct.
///
/// POURQUOI ELLE EXISTE, ET CE QU'ELLE ISOLE.
///
/// Un ecart tenace separait les deux chemins. Sur un morceau du crate, la sonde hors ligne
/// trouve 676 a 692 ms d'un bout a l'autre du fichier — un tempo stable a 87 BPM. Le meme
/// moteur, alimente par la carte son, publiait 113 BPM pendant treize pour cent du temps,
/// par episodes tenus de vingt a trente secondes.
///
/// Deux causes possibles ont ete ecartees par la mesure. Le son n'est pas en cause :
/// enregistrer ce que <c>parec</c> delivre, avec ses parametres exacts, puis passer cet
/// enregistrement dans la sonde redonne 681 a 687 ms. L'algorithme non plus, puisque c'est
/// le meme.
///
/// Restait la <b>cadence</b>. La sonde avale les fenetres aussi vite qu'elle peut et les
/// date au compte d'echantillons ; le direct les recoit au rythme de la carte son et les
/// date a l'horloge murale. Cette source reproduit le second regime sur une matiere dont on
/// connait la reponse — meme rythme, memes horodatages, mais aucune perte possible.
///
/// Elle sert aussi, au-dela du diagnostic, a rejouer un set enregistre dans toute la chaine
/// sans materiel : ce que la sonde ne permet pas, puisqu'elle court-circuite le serveur.
/// </summary>
public sealed class WavAudioSource : IAudioSource
{
    private readonly string _path;
    private readonly SpectrumAnalyzer _analyzer;
    private readonly bool _boucle;

    public WavAudioSource(string path, bool separate = false, bool boucle = true)
    {
        _path = path;
        _boucle = boucle;
        var (_, rate) = Wav.ReadMono(path, 0, 0.1);
        _analyzer = new SpectrumAnalyzer(rate, separate);
        SampleRate = rate;
    }

    public int SampleRate { get; }

    public string Name => $"fichier {Path.GetFileName(_path)}";

    public SpectrumAnalyzer Analyzer => _analyzer;

    public async IAsyncEnumerable<VisualFrame> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (mono, _) = Wav.ReadMono(_path, 0, double.MaxValue);
        var start = DateTime.UtcNow;
        const int hop = SpectrumAnalyzer.Window;

        var tour = 0;
        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i + hop <= mono.Length && !ct.IsCancellationRequested; i += hop)
            {
                // Le rythme reel : on attend que la fenetre soit « arrivee ». Sans cela on
                // rejouerait le morceau cent fois plus vite et l'on ne testerait rien.
                var du = (tour * (double)mono.Length + i) / SampleRate * 1000.0;
                var retard = du - (DateTime.UtcNow - start).TotalMilliseconds;
                if (retard > 1) await Task.Delay((int)retard, ct);

                // Meme base de temps que la capture : l'horloge murale, pas le compte
                // d'echantillons. C'est precisement la variable qu'on veut isoler.
                var t = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                yield return _analyzer.Analyze(mono.AsSpan(i, hop), t);
            }

            if (!_boucle) yield break;
            tour++;
        }
    }
}
