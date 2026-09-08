namespace Emotion.Signal;

/// <summary>
/// Ressort critiquement amorti, cote analyse.
///
/// POURQUOI LE LISSAGE DESCEND ICI. Il vivait dans le renderer : chaque grandeur continue
/// y traversait un ressort avant d'etre dessinee. Cela marchait tant qu'il n'y avait qu'un
/// renderer — mais l'unite CUDA devra reimplementer les memes ressorts, avec les memes
/// raideurs, et rien ne garantira qu'elles restent d'accord. Une regle qui vit en deux
/// endroits finit par vivre de deux facons ; c'est deja arrive ici avec le Camelot.
///
/// L'analyse envoie donc des valeurs <b>deja amorties</b>, et le rendu n'a plus qu'a
/// interpoler entre deux images. Le GPU ne calcule rien : il lit et affiche.
///
/// CE QUE CELA NE COUTE PAS. Aucune latence supplementaire. Le ressort existait deja et
/// avait deja son temps de reponse ; on le deplace, on ne l'ajoute pas. Un ressort a 47 Hz
/// puis interpole a 60 rend pratiquement le meme mouvement que le meme ressort a 60 Hz —
/// la difference tient dans le pas de simulation, pas dans la reponse.
///
/// CE QUI NE DESCEND PAS ICI. Les <b>evenements</b> — kick, clap, rupture — restent bruts
/// et instantanes. Les amortir les detruirait : une impulsion lissee n'est plus une
/// impulsion. Le renderer continue de les declencher lui-meme, et c'est la seule chose
/// qu'il calcule encore.
/// </summary>
public sealed class Damper
{
    private readonly float _k;
    private readonly float _c;
    private float _value;
    private float _velocity;

    /// <param name="stiffness">
    /// Raideur. Huit est mou et flottant, vingt est vif, quarante quasi direct. Les
    /// valeurs reprennent celles qu'employait le renderer, pour que le mouvement ne
    /// change pas en changeant de place.
    /// </param>
    /// <param name="start">valeur de depart.</param>
    public Damper(float stiffness, float start = 0f)
    {
        _k = stiffness;

        // Amortissement critique : c = 2·racine(k). C'est la valeur exacte qui ramene la
        // masse au repos le plus vite possible sans jamais depasser la cible. En dessous
        // elle oscillerait, au-dessus elle trainerait.
        _c = 2f * MathF.Sqrt(stiffness);
        _value = start;
    }

    public float Value => _value;

    public float Feed(float target, float dt)
    {
        var a = _k * (target - _value) - _c * _velocity;
        _velocity += a * dt;
        _value += _velocity * dt;
        return _value;
    }

    public void Reset(float start = 0f)
    {
        _value = start;
        _velocity = 0f;
    }
}
