using System.Text.Json;

namespace Emotion.Signal;

/// <summary>
/// Ce que le systeme sait d'un morceau, entre deux ecoutes.
///
/// LE PRINCIPE, ET IL EST PLUS IMPORTANT QUE SON IMPLEMENTATION.
///
/// Une ecoute n'est pas une session : c'est une contribution. Arreter un disque a la
/// vingt-quatrieme seconde, le relancer, et le laisser tourner ne doit pas remettre le
/// compteur a zero — la seconde ecoute <b>corrige</b> ce que la premiere avait etabli.
/// A la vingt-quatrieme seconde on dispose du meilleur portrait que vingt-quatre secondes
/// permettent ; a la vingt-cinquieme, il est meilleur, et il ne se degrade jamais.
///
/// C'est ce qui rend la preparation au casque payante. Les seize temps du cue ne servent
/// pas seulement a caler : ils constituent une connaissance qui passe au master avec le
/// disque. Le piano qui s'ajoute au master se calcule alors sur le tas, mais tout ce qui
/// avait deja ete etabli n'est pas recalcule — c'est autant de latence en moins au moment
/// ou elle coute le plus cher.
///
/// POURQUOI LA CONVERGENCE N'ETAIT PAS ACQUISE.
///
/// Il ne suffit pas de ranger des valeurs pour qu'ecouter plus longtemps serve. Tant que
/// chaque observation corrigeait le portrait d'une fraction fixe, la millieme pesait
/// autant que la dixieme : le portrait flottait autour de la bonne valeur sans jamais s'y
/// poser, et reprendre une ecoute n'ajoutait rien. Le pas decroissant de
/// <see cref="SourceIdentity"/> est ce qui transforme l'accumulation en convergence ; sans
/// lui, ce fichier ne ferait que deplacer du bruit d'une session a l'autre.
/// </summary>
/// <param name="Id">le morceau. Deux ecoutes du meme disque doivent porter le meme.</param>
/// <param name="Sources">le portrait de chaque registre.</param>
/// <param name="Bpm">le tempo etabli, ou zero. Il n'aura pas a etre recalcule.</param>
/// <param name="BpmObservations">sur combien de fenetres ce tempo repose.</param>
/// <param name="SecondsHeard">duree cumulee d'ecoute, toutes reprises confondues.</param>
public readonly record struct TrackKnowledge(
    string Id,
    SourcePortrait[] Sources,
    float Bpm,
    int BpmObservations,
    float SecondsHeard)
{
    public static TrackKnowledge Empty(string id) =>
        new(id, new SourcePortrait[Voices.Registers], 0f, 0, 0f);

    /// <summary>Y a-t-il quelque chose a reprendre, ou part-on de rien ?</summary>
    public bool Any => SecondsHeard > 0f;
}

/// <summary>
/// Range et reprend la connaissance des morceaux, un fichier par disque.
///
/// Un fichier par morceau et non une base : ce sont quelques centaines d'octets, ils se
/// lisent d'un coup au chargement du disque, et un fichier corrompu ne coute que le
/// morceau qu'il decrit. Le format est lisible — on doit pouvoir regarder ce que le
/// systeme croit savoir sans outil.
/// </summary>
public sealed class KnowledgeStore
{
    private readonly string _root;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
    };

    public KnowledgeStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Chemin du fichier d'un morceau. L'identifiant est nettoye : il vient d'un nom de
    /// fichier ou d'une fiche, et rien ne garantit qu'il tienne dans un nom de fichier.
    /// </summary>
    private string PathOf(string id)
    {
        var safe = new string(id.Select(
            c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length > 120) safe = safe[..120];
        return Path.Combine(_root, safe + ".json");
    }

    /// <summary>
    /// Reprend ce qu'on sait de ce morceau, ou une connaissance vide s'il est nouveau.
    ///
    /// Un fichier illisible est traite comme une absence : mieux vaut reapprendre que
    /// s'arreter, et un disque qui refuserait de se lancer parce qu'un cache est abime
    /// serait un mauvais echange pendant un set.
    /// </summary>
    public TrackKnowledge Load(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path)) return TrackKnowledge.Empty(id);

        try
        {
            var k = JsonSerializer.Deserialize<TrackKnowledge>(File.ReadAllText(path));
            if (k.Sources is not { Length: Voices.Registers }) return TrackKnowledge.Empty(id);
            return k;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return TrackKnowledge.Empty(id);
        }
    }

    /// <summary>
    /// Range ce qu'on vient d'apprendre. Une ecriture ratee est silencieuse : perdre un
    /// cache est sans consequence, interrompre un set n'en est pas une.
    /// </summary>
    public void Save(in TrackKnowledge knowledge)
    {
        try
        {
            File.WriteAllText(PathOf(knowledge.Id),
                              JsonSerializer.Serialize(knowledge, Format));
        }
        catch (IOException)
        {
        }
    }
}
