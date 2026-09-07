namespace Emotion.Signal;

/// <summary>
/// La tension : montee, puis rupture.
///
/// POURQUOI CA VAUT PLUS QUE TOUT LE RESTE. Chaque autre grandeur de ce projet est
/// <b>reactive</b> : on constate une attaque, on la dessine, et tout le travail consiste
/// a raccourcir le delai entre les deux. Une montee, elle, est <b>predictible</b> — c'est
/// meme sa definition. Trois grandeurs deja mesurees montent ensemble pendant huit a
/// seize mesures :
///
///   le centroide monte    les aigus s'ouvrent, les cymbales s'accumulent
///   les graves s'effacent le kick sort du mix pour mieux revenir
///   la densite monte      il se passe de plus en plus de choses
///
/// Aucune de ces trois n'est un evenement : ce sont des <b>pentes</b>. Un systeme qui les
/// lit sait ou va le morceau, et peut donc changer de motif <i>sur</i> la rupture au lieu
/// de courir apres elle. C'est le seul endroit du projet ou la latence de la chaine cesse
/// completement de compter — on n'attend plus rien, on est deja arrive.
///
/// CE QU'IL NE FAUT PAS EN FAIRE. Une pente n'est pas une certitude. Un morceau peut
/// monter puis retomber sans rien resoudre, et un visuel qui promettrait une explosion a
/// chaque montee mentirait la moitie du temps. La tension pilote donc une <b>tenue</b> —
/// la scene se tend — et jamais un evenement. Seule la rupture, constatee, en est un.
/// </summary>
public sealed class ArcDetector
{
    /// <summary>
    /// Memoire, en temps. Seize temps font quatre mesures : assez long pour qu'une pente
    /// se distingue du relief ordinaire d'un morceau, assez court pour qu'une montee de
    /// huit mesures soit deja lisible a mi-parcours.
    /// </summary>
    private const int Span = 16;

    /// <summary>
    /// Amplitude d'une montee franche, cumulee sur les trois pentes.
    ///
    /// MESURE, PAS DEVINE. En ecrivant la formule on imagine des pentes de trois
    /// dixiemes ; la sonde sur un vrai set donne p50 = 0,042 et un maximum de 0,149. Le
    /// diviseur initial de 0,5 ecrasait donc toute tension a moins d'un dixieme, et le
    /// detecteur n'aurait jamais rien signale de la soiree.
    /// </summary>
    private const float SlopeScale = 0.12f;

    private readonly float[] _bass = new float[Span];
    private readonly float[] _bright = new float[Span];
    private readonly float[] _busy = new float[Span];
    private int _n;

    // Accumulateurs de la fenetre en cours, vides a chaque temps.
    private float _sumBass, _sumBright, _sumBusy;
    private int _count;

    private float _buildup;
    private float _tensionPeak;
    private int _cooldown;

    public float Buildup => _buildup;

    /// <summary>Les trois pentes de la derniere mesure. Pour le reglage.</summary>
    public float SlopeBright { get; private set; }
    public float SlopeBass { get; private set; }
    public float SlopeBusy { get; private set; }
    public bool Drop { get; private set; }

    /// <summary>Une fenetre d'analyse. Appele a chaque image, entre deux temps.</summary>
    public void Feed(float bass, float brightness, float density)
    {
        _sumBass += bass;
        _sumBright += brightness;
        _sumBusy += density;
        _count++;
    }

    /// <summary>
    /// Un temps vient d'etre franchi : on ferme la mesure en cours et on juge la pente.
    ///
    /// Le raisonnement est deliberement grossier — la seconde moitie de la memoire contre
    /// la premiere. Une regression lineaire donnerait un chiffre plus joli sans rien
    /// changer a la decision, et couterait sur un chemin ou l'on compte les microsecondes.
    /// </summary>
    public void Advance()
    {
        Drop = false;
        if (_cooldown > 0) _cooldown--;
        if (_count == 0) return;

        Push(_bass, _sumBass / _count);
        Push(_bright, _sumBright / _count);
        Push(_busy, _sumBusy / _count);
        _sumBass = _sumBright = _sumBusy = 0f;
        _count = 0;

        if (_n < Span) { _n++; return; }

        var half = Span / 2;
        var dBass = Mean(_bass, half, Span) - Mean(_bass, 0, half);
        var dBright = Mean(_bright, half, Span) - Mean(_bright, 0, half);
        var dBusy = Mean(_busy, half, Span) - Mean(_busy, 0, half);

        // Les trois pentes tirent dans le meme sens musical mais pas dans le meme sens
        // arithmetique : les graves s'effacent quand les deux autres montent, d'ou le
        // signe. Le diviseur ramene une montee franche — de l'ordre de trois dixiemes
        // sur chaque grandeur — autour de l'unite.
        SlopeBright = dBright; SlopeBass = dBass; SlopeBusy = dBusy;

        // UNE MONTEE EST UNE CONVERGENCE, PAS UNE SOMME.
        //
        // Additionner les trois pentes suffirait si elles etaient independantes ; elles
        // ne le sont pas. Au repos elles oscillent de quelques centiemes dans des sens
        // quelconques, et une somme assez sensible pour voir une vraie montee est aussi
        // assez sensible pour additionner ce bruit-la. Ce qui distingue une montee n'est
        // pas l'amplitude — elle est faible — mais le fait que les trois disent la meme
        // chose en meme temps.
        //
        // On compte donc combien vont dans le sens d'une montee, et le carre de cette
        // proportion ecrase ce qui n'est pas unanime : deux pentes sur trois ne valent
        // plus que quatre neuviemes.
        var agree = (dBright > 0f ? 1 : 0) + (dBusy > 0f ? 1 : 0) + (dBass < 0f ? 1 : 0);
        var unity = agree / 3f;

        var raw = (dBright * 1.0f + dBusy * 0.8f - dBass * 1.2f) / SlopeScale * unity * unity;
        var target = Math.Clamp(raw, 0f, 1f);

        // Lissage asymetrique : la tension monte lentement, comme la montee qu'elle
        // decrit, mais retombe vite. Une tension qui s'attarderait apres la rupture
        // laisserait le visuel tendu alors que le morceau est deja reparti.
        _buildup += (target - _buildup) * (target > _buildup ? 0.12f : 0.35f);
        _tensionPeak = MathF.Max(_buildup, _tensionPeak * 0.97f);

        // La rupture : les graves reviennent d'un coup, apres une tension reelle.
        //
        // Exiger la tension prealable est ce qui separe un drop d'un simple accent. Sans
        // cette condition, chaque retour de kick apres une respiration d'une mesure
        // serait annonce comme un evenement majeur, et le mot perdrait son sens.
        var now = _bass[Span - 1];
        var before = Mean(_bass, Span - 5, Span - 1);
        if (_cooldown == 0 && _tensionPeak > 0.35f && before > 1e-4f && now > before * 1.6f)
        {
            Drop = true;
            _tensionPeak = 0f;
            _buildup = 0f;
            _cooldown = 16;   // quatre mesures : deux ruptures ne se suivent pas de si pres
        }
    }

    public void Reset()
    {
        _n = 0;
        _buildup = _tensionPeak = 0f;
        _cooldown = 0;
    }

    private static void Push(float[] a, float v)
    {
        Array.Copy(a, 1, a, 0, a.Length - 1);
        a[^1] = v;
    }

    private static float Mean(float[] a, int from, int to)
    {
        var s = 0f;
        for (var i = from; i < to; i++) s += a[i];
        return s / (to - from);
    }
}
