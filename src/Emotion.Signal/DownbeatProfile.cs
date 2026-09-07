namespace Emotion.Signal;

/// <summary>
/// Cherche le temps fort dans ce que <b>portent</b> les quatre temps, et non dans ce qui
/// s'y declenche.
///
/// POURQUOI LE VOTE PAR EVENEMENTS PLAFONNE. <see cref="BeatGrid"/> vote sur des
/// detections binaires : un kick est la ou il n'est pas. Or dans quantite de morceaux le
/// kick tombe sur les quatre temps, et il n'apprend alors rigoureusement rien. Le clap,
/// lui, tient les temps faibles mais ne distingue pas le 2 du 4 — kick sur 1 et 3 avec
/// clap sur 2 et 4 est <b>symetrique par decalage de deux temps</b>, et l'information
/// n'existe simplement pas dans la suite des detections. Restent le changement d'accord et
/// la rupture de section, tous deux rares. Mesure : temps fort nomme sur 38 a 74 % des
/// fenetres selon le passage.
///
/// CE QUI CHANGE ICI. Un kick present sur les quatre temps n'est pas quatre fois le meme
/// kick : celui du <b>1</b> est plus appuye, porte plus de grave, et c'est audible avant
/// d'etre mesurable. La presence ne distingue pas, l'amplitude si.
///
/// On accumule donc, pour chacune des quatre positions de la grille, le profil moyen de ce
/// qu'elle porte — energie grave, montee du registre du kick, mouvement harmonique — sur
/// des dizaines de mesures. Le temps fort est la position dont le profil se detache.
///
/// C'est la troisieme fois dans ce projet qu'un probleme cede en passant de l'evenement au
/// continu : le tempo par autocorrelation de l'enveloppe plutot que par ecarts entre
/// attaques, la structure longue par similarite de signatures plutot que par ruptures
/// detectees, et maintenant le temps fort.
/// </summary>
public sealed class DownbeatProfile
{
    /// <summary>
    /// Inertie des moyennes. Le temps fort est une propriete du morceau : rien ne presse,
    /// et une moyenne lente est ce qui permet a un ecart de quelques centiemes de se
    /// distinguer du bruit d'une mesure a l'autre.
    /// </summary>
    private const float Inertia = 0.995f;

    /// <summary>Ecart relatif au profil moyen valant certitude.</summary>
    private const float DecisiveLead = 0.22f;

    private readonly float[] _bass = new float[4];
    private readonly float[] _rise = new float[4];
    private readonly float[] _harmony = new float[4];
    private int _fed;

    public int Offset { get; private set; }
    public float Confidence { get; private set; }

    /// <summary>Les quatre scores, pour l'ecran de reglage.</summary>
    public IReadOnlyList<float> Scores => _score;

    private readonly float[] _score = new float[4];

    /// <summary>
    /// Une fenetre d'analyse.
    /// </summary>
    /// <param name="beatIndex">rang du temps courant dans la grille, non corrige.</param>
    /// <param name="bass">energie des bandes graves.</param>
    /// <param name="rise">montee du registre du kick sur cette fenetre.</param>
    /// <param name="harmony">mouvement du profil harmonique.</param>
    public void Feed(long beatIndex, float bass, float rise, float harmony)
    {
        var p = (int)(((beatIndex % 4) + 4) % 4);

        _bass[p] = _bass[p] * Inertia + bass * (1f - Inertia);
        _rise[p] = _rise[p] * Inertia + rise * (1f - Inertia);
        _harmony[p] = _harmony[p] * Inertia + harmony * (1f - Inertia);

        if (++_fed < 200) return;   // environ une dizaine de mesures
        Conclude();
    }

    public void Reset()
    {
        Array.Clear(_bass);
        Array.Clear(_rise);
        Array.Clear(_harmony);
        Array.Clear(_score);
        _fed = 0;
        Offset = 0;
        Confidence = 0f;
    }

    private void Conclude()
    {
        // Chaque grandeur est ramenee a son ecart <b>relatif</b> a sa propre moyenne sur
        // les quatre positions. Sans cela, l'energie grave — qui est cent fois plus grande
        // que le mouvement harmonique — deciderait seule, et les trois indices n'en
        // feraient qu'un.
        var scale = Relative(_bass, out var bass)
                  & Relative(_rise, out var rise)
                  & Relative(_harmony, out var harmony);

        if (!scale) { Confidence = 0f; return; }

        var best = 0;
        var bestVal = float.MinValue;
        var second = float.MinValue;

        for (var p = 0; p < 4; p++)
        {
            // Le grave pese le plus : c'est le seul des trois qui distingue le 1 du 3
            // dans un morceau ou le kick tombe partout, et c'est le cas le plus frequent.
            _score[p] = bass[p] * 1.0f + rise[p] * 0.5f + harmony[p] * 0.8f;

            if (_score[p] > bestVal) { second = bestVal; bestVal = _score[p]; best = p; }
            else if (_score[p] > second) second = _score[p];
        }

        Offset = best;
        Confidence = Math.Clamp((bestVal - second) / DecisiveLead, 0f, 1f);
    }

    /// <summary>
    /// Ramene quatre valeurs a leur ecart relatif a leur moyenne. Rend faux si la moyenne
    /// est trop faible pour que l'ecart veuille dire quelque chose — sur du silence, quatre
    /// zeros produiraient sinon des ecarts infinis.
    /// </summary>
    private static bool Relative(float[] v, out float[] result)
    {
        result = new float[4];
        var mean = (v[0] + v[1] + v[2] + v[3]) / 4f;
        if (mean < 1e-5f) return false;

        for (var p = 0; p < 4; p++) result[p] = (v[p] - mean) / mean;
        return true;
    }
}
