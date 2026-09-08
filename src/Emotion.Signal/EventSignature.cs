namespace Emotion.Signal;

/// <summary>
/// L'empreinte d'une frappe, prise a l'instant ou elle tombe.
///
/// CE QUI MANQUE AUJOURD'HUI, ET QUE LES BANDES NE DONNENT PAS.
///
/// Le systeme range les frappes par hauteur : ce qui tape dans les graves est un kick, dans
/// le medium un clap, dans l'aigu un charley. C'est une convention utile et grossiere — deux
/// percussions differentes qui vivent dans la meme tranche deviennent le meme evenement,
/// exactement comme deux instruments d'une meme octave devenaient une seule forme avant la
/// separation par le timbre.
///
/// Or une frappe a une couleur propre, et elle est stable : la meme caisse frappee deux fois
/// rend deux fois la meme empreinte, quel que soit le morceau. Trois grandeurs suffisent a
/// la decrire, et elles se lisent sur le spectre au moment de l'attaque.
///
/// <b>On ne nomme toujours rien.</b> Deux frappes de meme empreinte appartiennent a la meme
/// famille ; savoir laquelle est une caisse claire n'interesse pas le rendu, qui a seulement
/// besoin de ne pas confondre deux instruments differents.
/// </summary>
/// <param name="Brillance">
/// Centre de gravite du spectre a l'attaque, 0 sourd, 1 clair. C'est ce qui separe le plus
/// franchement un kick d'un charley — et deux caisses entre elles.
/// </param>
/// <param name="Etalement">
/// Largeur de l'energie autour de ce centre. Une peau accordee concentre, un bruit blanc
/// etale : c'est ce qui distingue un tom d'une cymbale de meme brillance.
/// </param>
/// <param name="Piquant">
/// Vitesse a laquelle l'energie retombe apres l'attaque, 0 pour une resonance longue, 1 pour
/// un claquement sec. Deux frappes peuvent avoir la meme couleur et ne pas durer pareil.
/// </param>
public readonly record struct EventSignature(float Brillance, float Etalement, float Piquant)
{
    /// <summary>
    /// Distance entre deux empreintes, dans un espace ou chaque axe compte autant.
    ///
    /// Une distance euclidienne suffit parce que les trois grandeurs sont deja normalisees
    /// entre 0 et 1 ; les ponderer reviendrait a decider d'avance laquelle compte le plus, et
    /// rien ne le justifie tant qu'on n'a pas mesure.
    /// </summary>
    public float DistanceA(in EventSignature autre)
    {
        var b = Brillance - autre.Brillance;
        var e = Etalement - autre.Etalement;
        var p = Piquant - autre.Piquant;
        return MathF.Sqrt(b * b + e * e + p * p);
    }
}

/// <summary>
/// Range les frappes en familles, sans jamais les nommer.
///
/// POURQUOI UN REGROUPEMENT EN LIGNE ET NON UNE CLASSIFICATION APPRISE.
///
/// Un classifieur entraine dirait « caisse claire » — et se tromperait des qu'un disque
/// sortirait de ce qu'il a vu. Ce qu'on veut est plus modeste et plus robuste : savoir que
/// <b>cette frappe-ci est la meme que celle d'il y a deux mesures</b>. Cela ne demande aucun
/// apprentissage prealable, seulement de comparer une empreinte a celles deja rencontrees.
///
/// Chaque famille garde le centre de ses membres, corrige a chaque nouvelle frappe avec un
/// pas decroissant — la meme moyenne courante que pour les portraits de sources, et pour la
/// meme raison : un pas fixe ferait flotter le centre indefiniment au lieu de le poser.
/// </summary>
public sealed class EventFamilies
{
    /// <summary>
    /// En deca de cette distance, deux frappes sont la meme chose.
    ///
    /// Un quart de l'espace : deux frappes qui different d'un quart sur un seul axe restent
    /// ensemble, ce qui absorbe la variation naturelle d'un meme instrument frappe plus ou
    /// moins fort. Au-dela elles se separent.
    /// </summary>
    private const float MemeFamille = 0.25f;

    /// <summary>
    /// Combien de familles au maximum.
    ///
    /// Huit : une batterie de ce repertoire en montre rarement plus, et au-dela on
    /// decouperait un meme instrument selon la force de la frappe. La famille la moins vue
    /// cede sa place quand une neuvieme se presente.
    /// </summary>
    public const int Max = 8;

    private readonly EventSignature[] _centres = new EventSignature[Max];
    private readonly int[] _vues = new int[Max];
    private int _connues;

    /// <summary>Combien de familles distinctes ont ete rencontrees.</summary>
    public int Connues => _connues;

    /// <summary>Combien de fois la famille de rang donne a frappe.</summary>
    public int VuesDe(int famille) => (uint)famille < Max ? _vues[famille] : 0;

    /// <summary>Le centre d'une famille, pour le diagnostic et pour la fiche.</summary>
    public EventSignature CentreDe(int famille) =>
        (uint)famille < Max ? _centres[famille] : default;

    /// <summary>
    /// Range une frappe et rend le rang de sa famille. Cree une famille si aucune ne lui
    /// ressemble, et remplace la moins vue si elles sont toutes prises.
    /// </summary>
    public int Ranger(in EventSignature s)
    {
        var meilleur = -1;
        var meilleureDistance = MemeFamille;

        for (var i = 0; i < _connues; i++)
        {
            var d = s.DistanceA(_centres[i]);
            if (d < meilleureDistance) { meilleureDistance = d; meilleur = i; }
        }

        if (meilleur < 0)
        {
            if (_connues < Max)
            {
                meilleur = _connues++;
                _centres[meilleur] = s;
                _vues[meilleur] = 1;
                return meilleur;
            }

            // Toutes prises : la moins vue cede. C'est elle qui a le plus de chances d'etre
            // un accident — une frappe isolee, un craquement de vinyle.
            meilleur = 0;
            for (var i = 1; i < Max; i++) if (_vues[i] < _vues[meilleur]) meilleur = i;
            _centres[meilleur] = s;
            _vues[meilleur] = 1;
            return meilleur;
        }

        // Moyenne courante : la millieme frappe corrige moins que la dixieme, donc le centre
        // se pose au lieu de flotter.
        _vues[meilleur]++;
        var pas = MathF.Max(0.01f, 1f / _vues[meilleur]);
        var c = _centres[meilleur];
        _centres[meilleur] = new EventSignature(
            c.Brillance + (s.Brillance - c.Brillance) * pas,
            c.Etalement + (s.Etalement - c.Etalement) * pas,
            c.Piquant + (s.Piquant - c.Piquant) * pas);

        return meilleur;
    }

    public void Reset()
    {
        Array.Clear(_centres);
        Array.Clear(_vues);
        _connues = 0;
    }
}
