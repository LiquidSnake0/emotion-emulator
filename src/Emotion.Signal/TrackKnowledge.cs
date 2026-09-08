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
/// EN MEMOIRE VIVE, ET NULLE PART AILLEURS.
///
/// Cette connaissance ne va pas sur le disque dur. Une premiere version ecrivait un
/// fichier par face toutes les dix secondes — 996 octets, ce qui parait indolore. La
/// mesure a dit autre chose : le cout median d'une image passait de <b>2,0 a 3,2 ms</b>,
/// soit soixante pour cent de plus, et le pire cas de 17 a 34 ms — au-dessus du pas de
/// 21 ms. Payer cela pour retrouver un disque la semaine prochaine est un mauvais
/// echange : ce qu'on optimise, c'est la soiree en cours.
///
/// Une face rangee est donc oubliee. Ce qui compte, c'est la duree pendant laquelle le
/// disque est sur une platine — et pendant ce temps-la, tout est deja en memoire.
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
