namespace Emotion.Signal;

/// <summary>
/// Ce qu'on savait deja du morceau, confronte a ce qu'il fait reellement.
///
/// L'IDEE, ET POURQUOI ELLE CHANGE LA NATURE DE LA MESURE.
///
/// Quand un disque a ete cale au casque, son tempo est deja connu : seize temps de cue ont
/// suffi a l'etablir, et le recalculer une fois passe au master serait payer deux fois la
/// meme chose — c'est precisement la latence qu'on cherche a supprimer. La fiche apporte
/// donc une <b>reference</b>, et l'analyse n'a plus a la retrouver.
///
/// Mais une reference n'est pas une verite. Un vinyle n'est pas un fichier : le plateau
/// derive, le pitch bouge sous les doigts, un disque chauffe. Selim le dit ainsi : « si le
/// bpm est a 87.6 sur mon cue, c'est bon, il faudra pas le recalculer — mais s'il passe a
/// 88.6, faudra qu'on le sente, qu'on le voie ». Une reference qui ferait taire la mesure
/// serait pire que pas de reference du tout : elle rendrait le systeme aveugle a
/// exactement ce qui se voit le plus.
///
/// Cette classe ne remplace donc rien. Elle publie l'<b>ecart</b>, et l'ecart est un
/// signal a part entiere.
///
/// POURQUOI ON PUBLIE UNE DERIVE ET PAS UN ECART DE TEMPO.
///
/// Un ecart en BPM ne dit rien a l'oeil. 87,6 contre 88,6, c'est 1,1 % — un chiffre qui a
/// l'air negligeable et qui ne l'est pas du tout. La periode passe de 685 a 677
/// millisecondes : <b>huit millisecondes perdues a chaque temps</b>. Sur les seize temps
/// du palier de Selim, cela fait 130 ms, soit un cinquieme de temps ; au bout d'une
/// minute, la grille a gliss d'un temps entier et le motif tombe a cote du son.
///
/// C'est cette accumulation qu'on publie, comptee en fractions de temps. Elle a la
/// propriete qu'un ecart brut n'a pas : elle grandit tant que la derive dure, donc elle
/// devient visible avant d'etre genante — et elle revient a zero des que les deux tempos
/// se rejoignent.
/// </summary>
public sealed class TempoReference
{
    /// <summary>
    /// Derive a partir de laquelle on considere que ca se voit, en fractions de temps.
    ///
    /// Un huitieme de temps, soit 86 ms a 87 BPM. C'est l'ordre de grandeur ou un motif
    /// cesse de paraitre lie a la frappe : au-dessous, l'oeil raccroche les deux ; au-dela,
    /// il les voit comme deux evenements separes.
    /// </summary>
    public const float Noticeable = 0.125f;

    /// <summary>Au-dela, la grille est franchement ailleurs : un quart de temps.</summary>
    public const float Blatant = 0.25f;

    /// <summary>
    /// De combien le tempo doit s'ecarter de la derniere annonce pour qu'on en fasse une
    /// nouvelle.
    ///
    /// LA VALEUR SORT DU PALIER DE SELIM, ELLE N'EST PAS CHOISIE.
    ///
    /// Son unite de travail fait seize temps. Sur seize temps, un ecart de <c>d</c> BPM
    /// deplace la grille de <c>16 · d / bpm</c> temps ; pour que ce deplacement atteigne le
    /// huitieme de temps a partir duquel l'oeil decroche, il faut <c>d = 87,6 · 0,125 / 16</c>,
    /// soit <b>0,68 BPM</b>. En dessous, l'annonce porterait sur un changement que personne
    /// ne pourrait voir sur la duree ou il compte.
    ///
    /// Selim avait avance 1 BPM en precisant l'avoir dit au jugé. La mesure le place un peu
    /// plus bas, et c'est cette valeur-la qu'on garde.
    /// </summary>
    public float Step { get; set; } = 0.68f;

    private long _lastMs = -1;
    private float _announced;

    /// <summary>
    /// Le tempo apporte par la fiche, ou null si le disque n'a pas ete prepare. Le poser
    /// ne coupe pas la mesure : il lui donne quelque chose a comparer.
    /// </summary>
    public float? Expected { get; set; }

    /// <summary>Derive accumulee, en fractions de temps. Signee : negatif si le disque traine.</summary>
    public float Drift { get; private set; }

    /// <summary>
    /// A quel point la derive se voit, 0 a 1. C'est ce que le renderer lit : il n'a pas a
    /// savoir ce qu'est un temps pour decider d'y reagir.
    /// </summary>
    public float Visible { get; private set; }

    /// <summary>Y a-t-il une reference a confronter.</summary>
    public bool Referenced => Expected is > 0f;

    /// <summary>
    /// Le tempo vient d'etre reannonce sur cette image.
    ///
    /// POURQUOI UNE ANNONCE EN PLUS DE LA DERIVE. Les deux disent des choses differentes et
    /// aucune ne remplace l'autre. La derive dit <i>de combien la grille a glisse</i> et
    /// grandit tant que l'ecart dure ; l'annonce dit <i>a quel moment ca a change</i> et ne
    /// dure qu'une image. Un renderer a besoin des deux : l'une pour deformer ce qui est a
    /// l'ecran, l'autre pour marquer l'instant.
    ///
    /// L'annonce ne part qu'au franchissement d'un pas — « on est a 87,9 », puis « 88,1 »,
    /// puis « 88,5 ». Un tempo qui tremble d'un centieme entre deux fenetres n'a rien a
    /// annoncer : ce serait un clignotement, pas une information.
    /// </summary>
    public bool Announced { get; private set; }

    /// <summary>La derniere valeur annoncee. C'est elle que porte le ping, pas la mesure brute.</summary>
    public float Announcement => _announced;

    /// <summary>
    /// Une image. <paramref name="measured"/> est le tempo lu dans le son, ou null tant
    /// qu'il n'est pas accroche — auquel cas on ne derive pas, on attend.
    /// </summary>
    public void Feed(float? measured, long tMs)
    {
        Announced = false;

        if (measured is { } first && _announced <= 0f)
        {
            // Premiere accroche : on annonce, meme sans fiche. Le renderer doit savoir sur
            // quoi il travaille des qu'on le sait nous-memes.
            _announced = first;
            Announced = true;
        }
        else if (measured is { } now && MathF.Abs(now - _announced) >= Step)
        {
            _announced = now;
            Announced = true;
        }

        if (_lastMs < 0) { _lastMs = tMs; return; }

        var dt = tMs - _lastMs;
        _lastMs = tMs;

        if (Expected is not { } expected || expected <= 0f || measured is not { } bpm || bpm <= 0f)
            return;

        // Combien de temps se sont ecoules selon chacun des deux tempos. Leur difference
        // est exactement le decalage que prend la grille, exprime dans l'unite qui compte.
        var beatsExpected = dt * expected / 60_000f;
        var beatsMeasured = dt * bpm / 60_000f;

        Drift += beatsMeasured - beatsExpected;

        // Une derive qui a cesse doit se resorber : le renderer ne peut pas rester marque
        // par un ecart d'il y a deux minutes. On la ramene doucement vers zero quand les
        // deux tempos se rejoignent, et pas autrement.
        if (MathF.Abs(bpm - expected) < 0.1f) Drift *= 0.995f;

        var magnitude = MathF.Abs(Drift);
        Visible = magnitude <= Noticeable
            ? 0f
            : MathF.Min(1f, (magnitude - Noticeable) / (Blatant - Noticeable));
    }

    /// <summary>
    /// Reprend a zero. A appeler quand un autre disque arrive : la derive du precedent ne
    /// le concerne pas.
    /// </summary>
    public void Reset(float? expected = null)
    {
        Expected = expected;
        Drift = 0f;
        Visible = 0f;
        _lastMs = -1;
    }
}
