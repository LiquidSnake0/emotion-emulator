using System.Diagnostics;

namespace Emotion.Signal;

/// <summary>
/// Fait tourner les voies et rend leur etat.
///
/// PARALLELISER N'EST PAS TOUJOURS PLUS RAPIDE, ET IL FAUT LE MESURER.
///
/// Repartir six calculs sur six coeurs coute une repartition : reveiller des fils,
/// distribuer, attendre le dernier. Cette depense est fixe — quelques dizaines de
/// microsecondes — quand le travail d'une voie, lui, se compte en microsecondes. En
/// dessous d'un certain volume, le sequentiel gagne toujours, et le parallelisme ne fait
/// qu'ajouter de la latence en pretendant en retirer.
///
/// Le pipeline sait donc faire les deux et <b>mesure lequel gagne</b> sur la machine qui
/// l'execute : il alterne les deux modes au demarrage, compare, puis s'en tient au
/// meilleur. Une decision prise sur la machine vaut mieux qu'une decision prise
/// d'avance — un portable et une tour ne repondent pas pareil.
///
/// CE QUI RENDRA LE PARALLELE INTERESSANT. Aujourd'hui une voie ne fait qu'amortir et
/// detecter. Le jour ou chacune portera sa propre transformee, son propre timbre, sa
/// propre grille, la balance s'inversera — et ce code n'aura pas a changer, seulement a
/// remesurer.
/// </summary>
public sealed class SourcePipeline
{
    private readonly ISourceLane[] _lanes;
    private readonly Stopwatch _watch = new();

    private long _sequentialTicks;
    private long _parallelTicks;
    private int _samples;
    private bool _decided;

    /// <summary>Nombre d'images comparees avant de trancher.</summary>
    private const int Trial = 200;

    public SourcePipeline(params ISourceLane[] lanes) => _lanes = lanes;

    /// <summary>
    /// Impose un mode au lieu de le mesurer. Sert aux tests, qui doivent pouvoir exiger
    /// le parallele pour verifier qu'aucune voie n'en corrompt une autre — une mesure
    /// qui choisirait le sequentiel sur la machine d'integration ne testerait rien.
    /// </summary>
    public void Force(bool parallel)
    {
        Parallel = parallel;
        _decided = true;
    }

    public IReadOnlyList<ISourceLane> Lanes => _lanes;

    /// <summary>Le mode retenu, une fois la mesure faite.</summary>
    public bool Parallel { get; private set; }

    /// <summary>Ce que la mesure a donne, en microsecondes par image.</summary>
    public (double Sequential, double Parallel) Cost =>
        _samples == 0
            ? (0, 0)
            : (_sequentialTicks / (double)_samples / Stopwatch.Frequency * 1e6 * 2,
               _parallelTicks / (double)_samples / Stopwatch.Frequency * 1e6 * 2);

    public void Feed(ReadOnlySpan<float> spectrum)
    {
        if (_decided)
        {
            if (Parallel) RunParallel(spectrum);
            else RunSequential(spectrum);
            return;
        }

        // Pendant l'essai, on alterne : une image sequentielle, une image parallele. Les
        // deux voient donc la meme matiere a tour de role, ce qui rend la comparaison
        // honnete — mesurer l'un sur un passage calme et l'autre sur un passage dense
        // ne comparerait que les passages.
        if ((_samples & 1) == 0)
        {
            _watch.Restart();
            RunSequential(spectrum);
            _sequentialTicks += _watch.ElapsedTicks;
        }
        else
        {
            _watch.Restart();
            RunParallel(spectrum);
            _parallelTicks += _watch.ElapsedTicks;
        }

        if (++_samples >= Trial)
        {
            Parallel = _parallelTicks < _sequentialTicks;
            _decided = true;
        }
    }

    private void RunSequential(ReadOnlySpan<float> spectrum)
    {
        foreach (var lane in _lanes) lane.Feed(spectrum);
    }

    private void RunParallel(ReadOnlySpan<float> spectrum)
    {
        // AUCUN VERROU, ET AUCUN N'EST NECESSAIRE.
        //
        // Chaque voie ne touche qu'a son propre etat et n'ecrit que dans sa zone : les
        // ecritures ne peuvent pas se rencontrer. C'est la separation des donnees qui
        // rend la concurrence sure, et non un arbitrage — un verrou ici ne protegerait
        // rien et couterait la moitie du gain.
        //
        // Le spectre est partage mais seulement lu. On le copie hors de la pile parce
        // qu'un Span ne franchit pas la frontiere d'une lambda.
        var copy = _buffer ??= new float[spectrum.Length];
        if (copy.Length < spectrum.Length) copy = _buffer = new float[spectrum.Length];
        spectrum.CopyTo(copy);

        var lanes = _lanes;
        System.Threading.Tasks.Parallel.For(0, lanes.Length,
            i => lanes[i].Feed(copy));
    }

    private float[]? _buffer;

    public void Reset()
    {
        foreach (var lane in _lanes) lane.Reset();
    }
}
