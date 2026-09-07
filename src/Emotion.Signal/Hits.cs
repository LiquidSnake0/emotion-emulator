namespace Emotion.Signal;

/// <summary>
/// Qui vient de frapper. Trois detecteurs sur trois registres, plutot qu'un seul sur
/// tout le spectre.
///
/// La raison est visuelle avant d'etre technique : « je ne vois pas sur quoi les
/// eclairs sont cales ». Un detecteur unique ne sait pas distinguer un kick d'un clap,
/// donc tout se declenche sur tout, et l'oeil ne raccroche le visuel a rien de ce qu'il
/// entend. En separant les registres, chaque evenement sonore trouve son effet :
///
///   kick  ->  la masse pulse, l'onde de choc part du centre
///   clap  ->  l'eclair
///   hat   ->  le scintillement
///
/// Un morceau peut declencher les trois sur la meme fenetre : ce sont trois booleens
/// independants, pas un choix.
/// </summary>
/// <param name="Kick">Attaque dans le bas, environ 30 a 150 Hz.</param>
/// <param name="Clap">
/// Attaque dans le medium large, environ 150 Hz a 1,5 kHz. C'est la zone du clap, de la
/// caisse claire et du rimshot — ce qui claque sans faire trembler le sol.
/// </param>
/// <param name="Hat">Attaque dans l'aigu, au-dessus de 5 kHz. Charleys et cymbales.</param>
public readonly record struct Hits(bool Kick, bool Clap, bool Hat)
{
    public static readonly Hits None = new(false, false, false);

    /// <summary>Quelque chose a frappe, quel que soit le registre.</summary>
    public bool Any => Kick || Clap || Hat;
}
