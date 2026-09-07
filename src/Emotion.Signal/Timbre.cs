namespace Emotion.Signal;

/// <summary>
/// La couleur du son, par opposition a ses evenements.
///
/// Tout le reste de l'analyse repond a la question « qu'est-ce qui vient de se passer ».
/// Aucune ne repond a « comment ca sonne en ce moment ». Or c'est precisement ce que le
/// DJ manipule le plus souvent : un <b>filtre passe-bas</b> qu'on ferme sur huit mesures
/// ne change ni le tempo, ni les attaques, ni les notes. Il ne change que le timbre — et
/// un systeme qui ne le mesure pas reste impassible pendant le geste le plus visible d'un
/// set.
///
/// Deux grandeurs classiques suffisent, et elles disent des choses differentes.
/// </summary>
/// <param name="Centroid">
/// Centre de gravite du spectre, normalise 0 a 1. C'est la <b>brillance</b> percue :
/// bas quand le son est sourd, haut quand il est clair. Il bouge continument avec
/// l'orchestration.
/// </param>
/// <param name="Rolloff">
/// Frequence sous laquelle se trouve 85 % de l'energie, normalisee. C'est la mesure la
/// plus directe d'un <b>filtre passe-bas</b> : quand la coupure descend, le rolloff la
/// suit presque exactement, la ou le centroide bouge plus mollement parce qu'une seule
/// raie grave suffit a le tirer vers le bas.
/// </param>
/// <param name="Openness">
/// A quel point le filtre est ouvert, 0 ferme, 1 grand ouvert. C'est le rolloff rapporte
/// a son maximum recent : un morceau naturellement sourd n'est pas un morceau filtre, et
/// seule la comparaison a lui-meme permet de distinguer les deux.
/// </param>
/// <param name="Density">
/// Evenements par seconde, lisse. Un passage « down » n'est pas seulement plus sourd, il
/// est plus vide : moins de frappes, moins de notes.
/// </param>
public readonly record struct Timbre(
    float Centroid,
    float Rolloff,
    float Openness,
    float Density);

/// <summary>
/// Suit la couleur du son au fil du morceau.
/// </summary>
public sealed class TimbreTracker
{
    private const float RolloffShare = 0.85f;

    private readonly int _sampleRate;
    private readonly int _window;

    /// <summary>
    /// Maximum recent du rolloff, avec oubli lent. C'est lui qui donne son sens a
    /// l'ouverture : sans reference propre au morceau, on ne saurait pas distinguer un
    /// disque naturellement sourd d'un disque filtre.
    /// </summary>
    private float _rolloffPeak = 0.05f;

    private float _density;
    private float _centroid;
    private float _rolloff;

    public TimbreTracker(int sampleRate, int window)
    {
        _sampleRate = sampleRate;
        _window = window;
    }

    /// <summary>
    /// Analyse un spectre et le nombre d'evenements survenus sur cette fenetre.
    /// </summary>
    public Timbre Feed(ReadOnlySpan<float> spectrum, int events)
    {
        var half = spectrum.Length;
        var nyquist = _sampleRate / 2f;

        // Centroide : moyenne des frequences ponderee par leur energie.
        double weighted = 0, total = 0;
        for (var i = 1; i < half; i++)
        {
            var mag = spectrum[i];
            weighted += (double)i * mag;
            total += mag;
        }

        if (total < 1e-9)
        {
            // Silence : on laisse les valeurs redescendre au lieu de les mettre a zero
            // d'un coup, sinon le visuel claquerait a chaque blanc entre deux disques.
            _centroid *= 0.98f;
            _rolloff *= 0.98f;
            _density *= 0.95f;
            return Snapshot();
        }

        var centroidBin = weighted / total;
        var centroidHz = (float)(centroidBin * _sampleRate / _window);

        // Echelle logarithmique : l'oreille entend le rapport entre deux frequences, pas
        // leur difference. Un centroide lineaire resterait colle en bas d'echelle.
        var cNorm = Clamp01(MathF.Log2(MathF.Max(centroidHz, 20f) / 20f) / MathF.Log2(nyquist / 20f));
        _centroid += (cNorm - _centroid) * 0.15f;

        // Rolloff : le bin sous lequel se trouvent 85 % de l'energie.
        var threshold = total * RolloffShare;
        double running = 0;
        var rollBin = half - 1;
        for (var i = 1; i < half; i++)
        {
            running += spectrum[i];
            if (running >= threshold) { rollBin = i; break; }
        }
        var rollHz = rollBin * _sampleRate / (float)_window;
        var rNorm = Clamp01(MathF.Log2(MathF.Max(rollHz, 20f) / 20f) / MathF.Log2(nyquist / 20f));
        _rolloff += (rNorm - _rolloff) * 0.18f;

        // Le maximum recent redescend tres lentement : un filtre ferme pendant seize
        // mesures ne doit pas devenir la nouvelle normale, sinon l'ouverture remonterait
        // toute seule et le geste du DJ disparaitrait de l'ecran.
        _rolloffPeak = MathF.Max(_rolloff, _rolloffPeak * 0.99985f);

        // Densite : evenements par seconde, ramenee sur une echelle utilisable. Un
        // passage calme n'est pas seulement plus sourd, il est plus vide.
        var perSecond = events * (_sampleRate / (float)_window);
        _density += (Clamp01(perSecond / 12f) - _density) * 0.04f;

        return Snapshot();
    }

    private Timbre Snapshot() => new(
        _centroid,
        _rolloff,
        Clamp01(_rolloff / MathF.Max(_rolloffPeak, 1e-3f)),
        _density);

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
