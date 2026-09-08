namespace Emotion.Signal;

/// <summary>
/// Ce que le systeme sait du disque en cours, et s'il en sait assez.
/// </summary>
/// <param name="Progress">Avancement, 0 a 1. Sert a l'afficher sur le telephone.</param>
/// <param name="Ready">Le feu vert est donne.</param>
/// <param name="Reason">« convergence » ou « plafond », vide tant que rien n'est pret.</param>
/// <param name="SecondsLeft">Secondes restantes avant le plafond.</param>
public readonly record struct Readiness(
    float Progress,
    bool Ready,
    string Reason,
    float SecondsLeft)
{
    public static Readiness None => new(0f, false, "", 0f);
}

/// <summary>
/// Le seuil de connaissance : quand annoncer qu'on en sait assez sur un disque.
///
/// LE MOMENT OU CETTE QUESTION SE POSE. Le DJ charge un disque au casque et cale. Quand
/// l'oreille lui dit que ca tient, il laisse tourner — et ce temps-la n'est pas perdu :
/// c'est celui dont le systeme a besoin pour apprendre le morceau entrant. La bascule
/// visuelle attend ce feu vert, parce qu'un visuel qui basculerait sur un morceau qu'il ne
/// connait pas encore n'aurait rien a montrer.
///
/// POURQUOI PAS UN POURCENTAGE DU MORCEAU. C'etait la premiere idee, et elle a un defaut
/// simple : <b>quinze pour cent d'une intro ne valent pas quinze pour cent d'un
/// refrain</b>. Un disque qui ouvre sur trente secondes de nappe n'aurait rien dit de sa
/// grille, et le systeme se serait declare pret sur du vide.
///
/// CE QU'ON MESURE A LA PLACE. Chaque estimateur est surveille non pas sur sa valeur mais
/// sur sa <b>stabilite</b> : a-t-il cesse de changer d'avis. Un tempo qui ne bouge plus
/// depuis plusieurs secondes est un tempo acquis, quel que soit l'endroit du morceau ou
/// l'on se trouve.
///
/// LE PLAFOND VIENT DU METIER. Trente secondes, parce que seize temps est le palier auquel
/// le DJ valide un calage a l'oreille et qu'il n'acceptera pas d'attendre davantage.
/// Passe ce delai, on annonce ce qu'on a — <b>un feu vert tardif ne sert a rien, la
/// transition sera deja passee</b>.
///
/// CE QUE LE PLAFOND EXCLUT, ET C'EST ASSUME. La structure longue demande deux phrases,
/// soit une quarantaine de secondes. Elle n'entrera donc pas dans ce que le GPU recoit a
/// la bascule ; elle continuera de se construire ensuite et servira plus tard dans le
/// morceau. Le systeme livre ce qu'il sait a l'instant ou l'on en a besoin.
/// </summary>
public sealed class KnowledgeGate
{
    /// <summary>Plafond, en secondes. Voir la note sur le metier ci-dessus.</summary>
    public const float CapSeconds = 30f;

    /// <summary>
    /// Fenetres pendant lesquelles une grandeur doit rester calme pour etre dite acquise.
    /// Quarante-sept font environ une seconde.
    /// </summary>
    private const int CalmFor = 47;

    private readonly Tracked[] _watched;
    private long _startMs = -1;
    private bool _announced;

    public Readiness Current { get; private set; } = Readiness.None;

    public KnowledgeGate()
    {
        // Ce qu'il faut savoir avant de basculer, et la variation sous laquelle chacun est
        // considere comme fixe. Les tolerances sont relatives a l'echelle de la grandeur :
        // un BPM sur un tempo, quelques centiemes sur une valeur normalisee.
        _watched =
        [
            new Tracked("tempo", 1.0f),
            new Tracked("couleur", 0.04f),
            new Tracked("basse", 0.06f),
            new Tracked("densite", 0.05f),
        ];
    }

    /// <summary>
    /// Une fenetre d'analyse.
    /// </summary>
    public Readiness Feed(long tMs, float? bpm, float centroid, float bass, float density)
    {
        if (_startMs < 0) _startMs = tMs;
        var elapsed = (tMs - _startMs) / 1000f;

        // Un tempo absent n'est pas un tempo stable : tant qu'il n'est pas publie, la
        // grandeur ne peut pas etre acquise, et son compteur repart de zero.
        _watched[0].Feed(bpm ?? float.NaN);
        _watched[1].Feed(centroid);
        _watched[2].Feed(bass);
        _watched[3].Feed(density);

        var settled = 0;
        foreach (var w in _watched) if (w.Settled) settled++;

        var progress = Math.Clamp(
            0.7f * settled / _watched.Length + 0.3f * elapsed / CapSeconds, 0f, 1f);

        if (!_announced)
        {
            if (settled == _watched.Length)
            {
                _announced = true;
                Current = new Readiness(1f, true, "convergence", 0f);
                return Current;
            }

            if (elapsed >= CapSeconds)
            {
                // On annonce ce qu'on a. Le renderer lira les confiances de chaque
                // estimateur pour savoir a quoi se fier — c'est a cela qu'elles servent.
                _announced = true;
                Current = new Readiness(1f, true, "plafond", 0f);
                return Current;
            }
        }

        Current = _announced
            ? new Readiness(1f, true, Current.Reason, 0f)
            : new Readiness(progress, false, "", MathF.Max(0f, CapSeconds - elapsed));

        return Current;
    }

    /// <summary>Nouveau disque : tout est a reapprendre.</summary>
    public void Reset()
    {
        foreach (var w in _watched) w.Reset();
        _startMs = -1;
        _announced = false;
        Current = Readiness.None;
    }

    /// <summary>Une grandeur surveillee sur sa stabilite, jamais sur sa valeur.</summary>
    private sealed class Tracked(string name, float tolerance)
    {
        public string Name { get; } = name;

        private float _reference;
        private bool _has;
        private int _calm;

        public bool Settled => _calm >= CalmFor;

        public void Feed(float value)
        {
            if (float.IsNaN(value)) { _calm = 0; return; }

            if (!_has) { _reference = value; _has = true; _calm = 0; return; }

            if (MathF.Abs(value - _reference) <= tolerance) _calm++;
            else { _calm = 0; _reference = value; }
        }

        public void Reset()
        {
            _has = false;
            _calm = 0;
        }
    }
}
