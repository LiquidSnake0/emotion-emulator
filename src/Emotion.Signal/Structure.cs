namespace Emotion.Signal;

/// <summary>
/// Ou l'on se trouve dans la <b>structure</b> du morceau, par opposition a ce qui vient
/// d'arriver.
///
/// Tout le reste de l'analyse vit dans l'instant : une attaque, un niveau, une couleur.
/// Un DJ, lui, ne pense jamais en instants ni meme en temps — il pense en <b>huit, seize,
/// trente-deux mesures</b>. C'est l'unite sur laquelle il cale ses fondus, ses filtres et
/// ses coupures, et c'est la seule que le systeme ignorait completement.
///
/// L'enjeu n'est pas cosmetique. Une montee dure huit a seize mesures et ses signaux
/// montent <b>ensemble</b> pendant tout ce temps : un systeme qui les lit sait ce qui
/// arrive avant que ca arrive. Le motif peut alors changer <i>sur</i> le drop plutot que
/// deux cents millisecondes apres — et la latence de la chaine cesse d'avoir la moindre
/// importance, puisqu'on n'attend plus l'evenement pour reagir.
/// </summary>
/// <param name="Beat">
/// Rang du temps dans la mesure, 0 a 3, ou -1 tant que le « 1 » n'est pas identifie.
/// Zero est le temps fort.
/// </param>
/// <param name="Bar">Rang de la mesure dans la phrase, 0 a <see cref="PhraseBars"/>-1.</param>
/// <param name="PhrasePos">
/// Position continue dans la phrase, 0 a 1. C'est elle qu'un visuel doit suivre pour
/// anticiper : a 0,9 la phrase se termine, quoi qu'il arrive dans le son.
/// </param>
/// <param name="Confidence">
/// A quel point le temps fort est etabli. Sous un demi, ne rien fonder dessus : mieux
/// vaut un visuel sans mesure qu'un visuel cale sur la mauvaise.
/// </param>
/// <param name="Buildup">Tension qui monte, 0 a 1. Voir <see cref="ArcDetector"/>.</param>
/// <param name="Drop">La rupture vient de tomber, sur cette fenetre.</param>
/// <param name="BarStart">Cette fenetre porte un debut de mesure.</param>
/// <param name="PhraseStart">Cette fenetre porte un debut de phrase.</param>
/// <param name="PhraseBars">Longueur de phrase mesuree.</param>
/// <param name="BarsToBoundary">Mesures avant la prochaine frontiere de phrase.</param>
/// <param name="SectionConfidence">Fiabilite de la structure longue.</param>
/// <param name="Trust">
/// A quel point le rendu peut se fier a tout ce qui precede, 0 a 1.
///
/// <b>C'est un seuil de rendu, pas d'ecoute.</b> L'analyse ne s'arrete jamais : elle
/// continue de chercher pendant qu'un disque saute ou qu'un calage se fait. Ce qui change
/// est ce que le renderer a le droit d'en faire — et le lui dire coute un octet, la ou
/// lui faire recalculer quoi que ce soit couterait des millisecondes.
///
/// Elle monte par paliers, et ce sont ceux du metier : deux temps, quatre temps, seize
/// temps. Voir <see cref="ContinuityWatch.Trust"/>.
/// </param>
public readonly record struct Structure(
    int Beat,
    int Bar,
    float PhrasePos,
    float Confidence,
    float Buildup,
    bool Drop,
    bool BarStart,
    bool PhraseStart,
    int PhraseBars = 8,   // = DefaultPhraseBars ; une valeur par defaut de parametre
                          // ne peut pas citer une constante declaree dans le corps
    int BarsToBoundary = 0,
    float SectionConfidence = 0f,
    float Trust = 1f)
{
    /// <summary>
    /// Longueur de phrase supposee tant que <see cref="SectionTracker"/> n'a pas tranche.
    /// La house et la techno se construisent en seize mesures, le hip-hop et la soul dont
    /// vit ce bac en huit — mais c'est desormais une valeur de depart et non une regle :
    /// la longueur reelle se mesure.
    /// </summary>
    public const int DefaultPhraseBars = 8;

    public static Structure None => new(-1, 0, 0f, 0f, 0f, false, false, false);
}
