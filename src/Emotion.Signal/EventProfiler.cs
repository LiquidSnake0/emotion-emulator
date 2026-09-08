namespace Emotion.Signal;

/// <summary>
/// Prend l'empreinte d'une frappe et la range dans sa famille.
///
/// LE MOMENT DE LA MESURE COMPTE AUTANT QUE LA MESURE.
///
/// Une frappe est un evenement bref : sa couleur est dans son attaque, pas dans ce qui
/// resonne apres. Mesurer une fenetre trop tard donne le timbre de la queue — la meme pour
/// toutes les percussions d'un meme mix, puisque c'est la reverberation de la salle. On
/// prend donc le spectre <b>de la fenetre ou l'attaque tombe</b>, et l'on regarde la
/// suivante seulement pour savoir a quelle vitesse elle retombe.
/// </summary>
public sealed class EventProfiler
{
    private readonly int _bins;
    private readonly float[] _attaque;
    private float _energieAttaque;
    private bool _enAttente;

    /// <summary>Les familles rencontrees, communes a toutes les frappes.</summary>
    public EventFamilies Familles { get; } = new();

    /// <summary>La derniere empreinte prise, et la famille ou elle est tombee.</summary>
    public EventSignature Derniere { get; private set; }
    public int DerniereFamille { get; private set; } = -1;

    public EventProfiler(int bins)
    {
        _bins = bins;
        _attaque = new float[bins];
    }

    /// <summary>
    /// Une image de spectre, et si une frappe vient d'y etre detectee.
    ///
    /// Rend le rang de la famille quand une empreinte vient d'etre complete, et -1 sinon.
    /// Une empreinte demande deux fenetres : celle de l'attaque et la suivante.
    /// </summary>
    public int Feed(ReadOnlySpan<float> spectre, bool frappe)
    {
        if (_enAttente)
        {
            // La fenetre d'apres : on ne veut d'elle que le rapport d'energie, qui dit a
            // quelle vitesse la frappe retombe.
            var apres = 0f;
            for (var i = 0; i < _bins && i < spectre.Length; i++) apres += spectre[i];

            var piquant = _energieAttaque > 1e-6f
                ? Clamp01(1f - apres / _energieAttaque)
                : 0f;

            Derniere = Empreinte(piquant);
            DerniereFamille = Familles.Ranger(Derniere);
            _enAttente = false;
            return DerniereFamille;
        }

        if (!frappe) return -1;

        // L'attaque : on garde le spectre tel quel, il portera la couleur.
        var n = Math.Min(_bins, spectre.Length);
        _energieAttaque = 0f;
        for (var i = 0; i < n; i++)
        {
            _attaque[i] = spectre[i];
            _energieAttaque += spectre[i];
        }
        for (var i = n; i < _bins; i++) _attaque[i] = 0f;

        _enAttente = true;
        return -1;
    }

    /// <summary>Les deux grandeurs spectrales, tirees de la fenetre d'attaque.</summary>
    private EventSignature Empreinte(float piquant)
    {
        if (_energieAttaque < 1e-6f) return new EventSignature(0.5f, 0.5f, piquant);

        // Centre de gravite, en echelle logarithmique : l'oreille entend des rapports, et
        // une echelle lineaire collerait toutes les percussions en bas du spectre.
        double poids = 0;
        for (var i = 1; i < _bins; i++) poids += _attaque[i] * MathF.Log2(i + 1);

        var etendue = MathF.Log2(_bins + 1);
        var brillance = Clamp01((float)(poids / _energieAttaque) / etendue);

        // Etalement : ecart moyen au centre, sur la meme echelle.
        double dispersion = 0;
        var centre = (float)(poids / _energieAttaque);
        for (var i = 1; i < _bins; i++)
            dispersion += _attaque[i] * MathF.Abs(MathF.Log2(i + 1) - centre);

        var etalement = Clamp01((float)(dispersion / _energieAttaque) / (etendue * 0.5f));

        return new EventSignature(brillance, etalement, piquant);
    }

    public void Reset()
    {
        Array.Clear(_attaque);
        _energieAttaque = 0f;
        _enAttente = false;
        DerniereFamille = -1;
        Familles.Reset();
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
