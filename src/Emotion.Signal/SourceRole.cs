namespace Emotion.Signal;

/// <summary>
/// Ce qu'une source fait VIS-A-VIS DE LA GRILLE : le rang qu'elle tient dans l'orchestre.
///
/// L'IMAGE EST CELLE DU DJ, ET ELLE EST JUSTE.
///
/// « On a un orchestre : le mec qui fait le tambour joue le metronome pour les autres,
/// celui au saxophone est charge de jouer les memes notes mais a des instants precis. »
///
/// Le systeme decrivait chaque source comme si elles etaient interchangeables — un niveau,
/// une hauteur, une enveloppe. Or elles ne tiennent pas le meme role, et ce role se mesure :
/// il suffit de replier les frappes d'une source sur la periode du temps et de regarder ou
/// elles tombent.
///
///   METRONOME   elle marque presque chaque temps. C'est elle qui tient l'orchestre.
///   PONCTUEL    elle marque une position PRECISE de la mesure, et toujours la meme —
///               la caisse claire sur deux et quatre, un accord sur le « et » de trois.
///   CONTINU     elle n'a aucune relation a la grille. Un souffle, une nappe.
///
/// CE QUE CA PERMET, ET QUI MANQUAIT.
///
/// La grille ne se cale que sur le kick : un seul temoin. Quand il se retire — et le DJ
/// l'observe, « des fois tout est creux sauf une melodie » — il ne reste rien. Avec les
/// roles, on sait lesquelles des six autres sources peuvent temoigner a sa place : toutes
/// celles qui sont metronomes marquent le meme temps que lui.
///
/// Et pour celles qui se retirent, on sait OU ELLES SERAIENT. Une source ponctuelle qui
/// tombait sur le troisieme temps y retombera ; on peut donc la montrer en creux pendant
/// son absence au lieu de faire comme si elle n'avait jamais existe.
///
/// LE NIVEAU DE HASARD SE CALCULE, il ne se devine pas. Pour n frappes posees au hasard, la
/// concentration vaut environ racine(pi) / (2 racine(n)). On exige le double, comme partout
/// ailleurs dans ce projet.
/// </summary>
public sealed class SourceRoles
{
    /// <summary>Cases du repli, par temps. Vingt-quatre donnent 29 ms a 87 BPM.</summary>
    public const int Cases = 24;

    /// <summary>
    /// Sur combien de temps on observe. Quarante-huit : douze mesures.
    ///
    /// LA MEMOIRE EST DICTEE PAR LE ROLE LE PLUS RARE, ET SEIZE NE SUFFISAIT PAS.
    ///
    /// Une source ponctuelle est clairsemee par definition : un accord une fois par mesure
    /// donne une frappe tous les quatre temps. Sur seize temps de memoire, elle n'en
    /// accumule que quatre — sous le seuil — et restait donc eternellement « inconnue ».
    /// C'est-a-dire que la mesure ne pouvait pas reconnaitre precisement le role qu'elle
    /// existe pour reconnaitre.
    ///
    /// Douze mesures en donnent une douzaine, ce qui passe le seuil sans exiger d'entendre
    /// la moitie du morceau. Et un role est une propriete de l'ARRANGEMENT : il change aux
    /// frontieres de section, pas d'une mesure a l'autre — s'adapter en douze mesures n'est
    /// donc pas lent, c'est juste.
    /// </summary>
    public const float MemoireTemps = 48f;

    /// <summary>
    /// Frappes par temps au-dela desquelles une source tient le metronome.
    ///
    /// Une source qui marque un temps sur deux tient encore la mesure ; une qui en marque
    /// un sur quatre ponctue. La frontiere est a un demi, et elle n'est pas arbitraire :
    /// c'est le point ou l'on cesse de pouvoir predire le temps suivant a partir d'elle
    /// seule.
    /// </summary>
    public const float ParTempsMetronome = 0.5f;

    /// <summary>Frappes minimales avant de se prononcer. En dessous, on dit qu'on ne sait pas.</summary>
    public const int Assez = 8;

    public const byte Inconnu = 0;
    public const byte Metronome = 1;
    public const byte Ponctuel = 2;
    public const byte Continu = 3;

    private readonly float[][] _cases;
    private readonly float[] _frappes;
    private readonly float[] _temps;
    private double _phase;
    private long _lastMs = -1;
    private float _beatMs = 690f;

    public SourceRoles(int sources)
    {
        _cases = new float[sources][];
        for (var i = 0; i < sources; i++) _cases[i] = new float[Cases];
        _frappes = new float[sources];
        _temps = new float[sources];
    }

    /// <summary>Une fenetre d'analyse. Les frappes de cette fenetre, source par source.</summary>
    public void Feed(long tMs, float? bpm, ReadOnlySpan<bool> frappes)
    {
        if (bpm is { } b && b > 20f && b < 400f)
            _beatMs += (60_000f / b - _beatMs) * 0.05f;

        if (_lastMs < 0) { _lastMs = tMs; return; }
        var dt = tMs - _lastMs;
        _lastMs = tMs;
        if (dt <= 0 || dt > 1000) return;

        _phase += dt / _beatMs;
        var tours = (float)Math.Floor(_phase);
        _phase -= tours;

        var oubli = MathF.Pow(0.5f, dt / (MemoireTemps * _beatMs));
        var mien = (int)(_phase * Cases) % Cases;

        for (var s = 0; s < _cases.Length; s++)
        {
            var t = _cases[s];
            for (var i = 0; i < Cases; i++) t[i] *= oubli;
            _frappes[s] *= oubli;
            _temps[s] = _temps[s] * oubli + tours;

            if (s < frappes.Length && frappes[s])
            {
                t[mien] += 1f;
                _frappes[s] += 1f;
            }
        }
    }

    /// <summary>Le role d'une source, ou <see cref="Inconnu"/>.</summary>
    public byte Role(int rang)
    {
        if ((uint)rang >= (uint)_cases.Length || _frappes[rang] < Assez) return Inconnu;

        var (r, _) = Concentration(rang);
        var hasard = MathF.Sqrt(MathF.PI) / (2f * MathF.Sqrt(_frappes[rang]));

        // Sans concentration, la source ne parle pas de la grille : elle joue a cote, ou
        // elle joue tout le temps. Dans les deux cas elle ne temoigne de rien.
        if (r < 2f * hasard) return Continu;

        var parTemps = _temps[rang] > 1f ? _frappes[rang] / _temps[rang] : 0f;
        return parTemps >= ParTempsMetronome ? Metronome : Ponctuel;
    }

    /// <summary>
    /// Ou la source tombe dans le temps, 0 a 1. C'est la qu'elle reviendra.
    ///
    /// Pour un metronome, c'est le temps lui-meme. Pour une source ponctuelle, c'est sa
    /// place — et c'est ce qui permet de la montrer en creux pendant qu'elle se tait.
    /// </summary>
    public float Place(int rang)
    {
        if ((uint)rang >= (uint)_cases.Length) return 0f;
        var (_, rang_) = Concentration(rang);
        return rang_;
    }

    /// <summary>A quel point elle tient sa place, 0 a 1.</summary>
    public float Tenue(int rang)
    {
        if ((uint)rang >= (uint)_cases.Length || _frappes[rang] < Assez) return 0f;
        var (r, _) = Concentration(rang);
        return Math.Clamp(r, 0f, 1f);
    }

    /// <summary>Concentration de Rayleigh du repli, et la place du paquet.</summary>
    private (float R, float Place) Concentration(int rang)
    {
        var t = _cases[rang];
        float sx = 0, sy = 0, n = 0;
        for (var i = 0; i < Cases; i++)
        {
            var a = 2f * MathF.PI * i / Cases;
            sx += t[i] * MathF.Cos(a);
            sy += t[i] * MathF.Sin(a);
            n += t[i];
        }
        if (n <= 0f) return (0f, 0f);
        var angle = MathF.Atan2(sy, sx);
        if (angle < 0) angle += 2f * MathF.PI;
        return (MathF.Sqrt(sx * sx + sy * sy) / n, angle / (2f * MathF.PI));
    }

    /// <summary>Ou en est le temps courant, 0 a 1. Pour situer une source absente.</summary>
    public float Phase => (float)_phase;

    public void Reset()
    {
        foreach (var t in _cases) Array.Clear(t);
        Array.Clear(_frappes);
        Array.Clear(_temps);
        _phase = 0;
        _lastMs = -1;
    }
}
