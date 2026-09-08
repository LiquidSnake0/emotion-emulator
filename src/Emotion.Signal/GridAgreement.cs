namespace Emotion.Signal;

/// <summary>
/// Verifie le tempo par une voie qui ne le connait pas.
///
/// LE PROBLEME QUE CELA RESOUT : ON N'A AUCUNE VERITE.
///
/// Tout le projet mesure, mais rien ne dit si le tempo trouve est le bon — il n'existe pas
/// d'annotation, et l'oreille de Selim n'est pas disponible a chaque fenetre. La confiance
/// publiee jusqu'ici vient de l'autocorrelation elle-meme : elle dit a quel point le pic
/// choisi ressort, pas s'il est au bon endroit. Une confiance calculee par celui qu'on veut
/// verifier ne verifie rien.
///
/// LES FAMILLES DE FRAPPES DONNENT CETTE SECONDE VOIE.
///
/// Elles sont formees sur le timbre, sans jamais consulter le tempo. Or un instrument de
/// percussion joue en mesure : ses intervalles tombent sur des multiples ou des divisions du
/// temps. Si les familles battent a des rapports francs du tempo detecte — une fois, deux
/// fois, une demi-fois — c'est que les deux voies sont d'accord. Si elles battent a des
/// rapports batards, l'une des deux se trompe, et il vaut mieux le savoir.
///
/// Mesure sur un morceau du crate a 87,4 BPM : les familles battent a x0,97, x1,89, x2,01 et
/// x0,55 — quatre rapports francs, le tempo est confirme. Sur un enregistrement de set, elles
/// battent a x4,45 et x1,25, et le desaccord se voit.
/// </summary>
public sealed class GridAgreement
{
    /// <summary>
    /// Les rapports qu'une percussion joue naturellement contre le temps.
    ///
    /// La ronde, la blanche, le temps, la croche, le triolet et la double. Au-dela on
    /// accepterait a peu pres n'importe quoi, ce qui reviendrait a ne rien verifier.
    /// </summary>
    private static readonly float[] Francs = [0.25f, 0.5f, 1f, 1.5f, 2f, 3f, 4f];

    /// <summary>Tolerance sur un rapport, en fraction. Cinq pour cent.</summary>
    private const float Tolerance = 0.05f;

    /// <summary>Frappes minimales pour qu'une famille ait voix au chapitre.</summary>
    private const int Assez = 8;

    private readonly List<long>[] _instants;
    private readonly EventFamilies _familles;

    /// <summary>
    /// A quel point les familles confirment le tempo, 0 a 1.
    ///
    /// Chaque famille assez vue vote, et son vote pese son nombre de frappes : une famille
    /// qui a frappe deux cents fois en sait plus qu'une qui en a frappe dix.
    /// </summary>
    public float Accord { get; private set; }

    /// <summary>Combien de familles ont pu voter.</summary>
    public int Votantes { get; private set; }

    public GridAgreement(EventFamilies familles)
    {
        _familles = familles;
        _instants = new List<long>[EventFamilies.Max];
        for (var i = 0; i < _instants.Length; i++) _instants[i] = new List<long>(64);
    }

    /// <summary>Une frappe vient d'etre rangee dans une famille.</summary>
    public void Frappe(int famille, long tMs)
    {
        if ((uint)famille >= EventFamilies.Max) return;

        var l = _instants[famille];
        l.Add(tMs);

        // On ne garde qu'une fenetre glissante : un morceau change, et les intervalles d'il
        // y a deux minutes ne disent rien de ceux d'a present.
        if (l.Count > 64) l.RemoveAt(0);
    }

    /// <summary>Recalcule l'accord avec le tempo courant. A appeler rarement, pas par image.</summary>
    public void Juger(float? bpm)
    {
        if (bpm is not { } b || b <= 0f) { Accord = 0f; Votantes = 0; return; }

        var tempsMs = 60_000f / b;
        float poidsTotal = 0, accordTotal = 0;
        Votantes = 0;

        for (var f = 0; f < EventFamilies.Max; f++)
        {
            var l = _instants[f];
            if (l.Count < Assez) continue;

            var median = MedianIntervalle(l);
            if (median <= 0f) continue;

            var rapport = tempsMs / median;      // combien de frappes par temps
            var ecart = float.MaxValue;
            foreach (var franc in Francs)
                ecart = MathF.Min(ecart, MathF.Abs(rapport - franc) / franc);

            // Un ecart nul vaut 1, un ecart d'une tolerance vaut 0. Entre les deux, une
            // decroissance lineaire : mieux vaut un vote nuance qu'un verdict binaire.
            var vote = MathF.Max(0f, 1f - ecart / Tolerance);
            var poids = _familles.VuesDe(f);

            accordTotal += vote * poids;
            poidsTotal += poids;
            Votantes++;
        }

        Accord = poidsTotal > 0 ? accordTotal / poidsTotal : 0f;
    }

    private static float MedianIntervalle(List<long> instants)
    {
        if (instants.Count < 2) return 0f;

        var iv = new float[instants.Count - 1];
        for (var i = 1; i < instants.Count; i++) iv[i - 1] = instants[i] - instants[i - 1];
        Array.Sort(iv);
        return iv[iv.Length / 2];
    }

    public void Reset()
    {
        foreach (var l in _instants) l.Clear();
        Accord = 0f;
        Votantes = 0;
    }
}
