namespace Emotion.Signal;

/// <summary>
/// Deduit le tempo des ecarts entre attaques, par vote plutot que par moyenne.
///
/// Une moyenne serait detruite par une seule attaque manquee, qui doublerait un ecart.
/// Un vote sur des intervalles arrondis laisse au contraire les erreurs se disperser
/// pendant que la bonne valeur s'accumule.
///
/// <b>Le tempo n'est jamais necessaire.</b> Il reste nul tant qu'il n'est pas sur, et
/// le renderer sait tourner sans lui : c'est ce qui permet de projeter des les
/// premieres secondes d'un morceau, et de ne rien casser pendant un fondu ou les
/// attaques de deux disques se melangent.
/// </summary>
public sealed class TempoEstimator
{
    private const int Keep = 24;             // ecarts conserves
    private const float MinBpm = 60f;
    private const float MaxBpm = 180f;

    private readonly List<long> _gaps = new();
    private long _lastOnset = -1;
    private long _anchor = -1;               // derniere attaque retenue, origine de la phase

    public float? Bpm { get; private set; }

    /// <summary>Enregistre une attaque a l'instant donne.</summary>
    public void Mark(long tMs)
    {
        _anchor = tMs;

        if (_lastOnset >= 0)
        {
            var gap = tMs - _lastOnset;
            var bpm = 60_000f / gap;

            // On ne garde que des ecarts musicalement plausibles : un ecart aberrant
            // ne doit pas polluer le vote.
            if (bpm >= MinBpm && bpm <= MaxBpm)
            {
                _gaps.Add(gap);
                if (_gaps.Count > Keep) _gaps.RemoveAt(0);
                Vote();
            }
        }

        _lastOnset = tMs;
    }

    /// <summary>
    /// Position dans la mesure de quatre temps, ou nul si le tempo n'est pas accroche.
    /// Calculee depuis la derniere attaque plutot que depuis le demarrage : elle se
    /// recale donc toute seule a chaque frappe, au lieu de deriver.
    /// </summary>
    public float? Phase(long tMs)
    {
        if (Bpm is not { } bpm || _anchor < 0) return null;
        var beatMs = 60_000f / bpm;
        var beats = (tMs - _anchor) / beatMs;
        var m = beats % 4f;
        if (m < 0) m += 4f;
        return m / 4f;
    }

    /// <summary>
    /// Vote sur les ecarts arrondis a dix millisecondes. Il faut au moins un tiers des
    /// ecarts d'accord pour declarer un tempo : en dessous, on prefere ne rien dire.
    /// </summary>
    private void Vote()
    {
        if (_gaps.Count < 6) return;

        var buckets = new Dictionary<long, int>();
        foreach (var g in _gaps)
        {
            var k = (g + 5) / 10 * 10;
            buckets[k] = buckets.GetValueOrDefault(k) + 1;
        }

        var best = 0L;
        var bestCount = 0;
        foreach (var (k, c) in buckets)
            if (c > bestCount) { bestCount = c; best = k; }

        Bpm = bestCount * 3 >= _gaps.Count && best > 0
            ? 60_000f / best
            : null;
    }
}
