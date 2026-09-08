using Emotion.Pulse;
using System.Globalization;

// Mesure de pulsation : ces instants forment-ils un pouls, et est-ce le bon ?
//
// Les deux mesures que le projet avait ne repondaient pas a cette question. Les
// indicateurs internes se notent contre une grille calee sur ce qu'ils notent. La
// confrontation a aubio dit « est-ce un vrai evenement », jamais « est-ce le bon ».
//
//   dotnet run --project tools/Emotion.Pulse -- <reference.txt> <candidat.txt> [autres...]
//
// Un fichier par ligne d'instants en secondes — ce que rendent aubioonset et l'option
// « instants= » de la sonde.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: pulse <candidat.txt> [candidat2.txt ...]");
    Console.Error.WriteLine("       pulse --contre <reference.txt> <candidat.txt> [...]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Sans reference : chaque suite est jugee sur son propre pouls.");
    Console.Error.WriteLine("  Avec --contre  : le pouls vient de la reference, les autres y sont notes.");
    Console.Error.WriteLine("  Un instant par ligne, en secondes.");
    return 1;
}

static List<double> Lire(string chemin) =>
    File.ReadLines(chemin)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => double.Parse(l.Trim(), CultureInfo.InvariantCulture))
        .OrderBy(x => x)
        .ToList();

// AVEC UNE REFERENCE, OU SANS. Les deux repondent a des questions differentes, et il a
// fallu constater l'echec de la premiere pour comprendre l'interet de la seconde.
//
// Avec reference, on demande : « ces frappes tombent-elles sur LE pouls » — celui qu'un
// autre a trouve. C'est la question qu'on voulait poser. Encore faut-il une reference qui
// pulse : le train d'attaques d'aubioonset, lui, n'a aucune pulsation (accord avec son
// propre pouls : +0,03 a +0,17 selon les morceaux), parce qu'il detecte tout ce qui bouge
// et non les temps. Le plafond affiche le dit, et l'outil refuse de conclure sans lui.
//
// Sans reference, on demande : « ces frappes forment-elles UN pouls » — sans dire lequel.
// C'est moins ambitieux et parfaitement suffisant pour comparer deux detecteurs sur la
// meme matiere, puisque aucun ne consulte alors la grille du systeme.
var contre = args.Length > 1 && args[0] == "--contre";
var fichiers = contre ? args.Skip(2).ToArray() : args;

Pulsation.Pouls? impose = null;
if (contre)
{
    var reference = Lire(args[1]);
    var pouls = Pulsation.Chercher(reference);
    Console.WriteLine($"reference : {Path.GetFileName(args[1])}  ·  {reference.Count} instants");
    if (pouls.Periode <= 0)
    {
        Console.Error.WriteLine("pas assez d'instants dans la reference pour en tirer un pouls");
        return 1;
    }

    Console.WriteLine($"  pouls trouve   {pouls.Bpm,6:F1} BPM  ({pouls.Periode * 1000:F0} ms)" +
                      $"  ·  force {pouls.Force:F3}  ·  {pouls.Rapport:F1} fois le hasard");

    // La reference sur son propre pouls : c'est le plafond. Aucun candidat ne peut
    // raisonnablement faire mieux, et un plafond bas signale une reference sans
    // pulsation — auquel cas la comparaison ne veut rien dire et il faut le voir.
    var plafond = Pulsation.Accord(reference, pouls);
    Console.WriteLine($"  accord avec elle-meme : {plafond:+0.000;-0.000}   (le plafond)");
    if (plafond < 0.15)
        Console.WriteLine("  ATTENTION : cette reference n'a pas de pulsation nette. Ne rien conclure.");
    Console.WriteLine();
    impose = pouls;
}

Console.WriteLine($"{"suite",-24}{"force",8}{"fois",7}{"stable",8}{"pouls",14}{"fen",6}" +
                  (contre ? $"{"accord",9}{"periode",16}" : ""));
Console.WriteLine(new string('-', contre ? 92 : 67));

foreach (var chemin in fichiers)
{
    var nom = Path.GetFileNameWithoutExtension(chemin);
    if (!File.Exists(chemin)) { Console.WriteLine($"{nom,-24}  fichier absent"); continue; }

    var t = Lire(chemin);
    if (t.Count < 8) { Console.WriteLine($"{nom,-24}  {t.Count} instants, trop peu"); continue; }

    var (sien, stabilite, fenetres) = Pulsation.ChercherLocal(t);
    if (sien.Periode <= 0) { Console.WriteLine($"{nom,-24}  pas assez de matiere"); continue; }

    var ligne = $"{nom,-24}{sien.Force,8:F3}{sien.Rapport,7:F1}{100 * stabilite,8:F0}%" +
                $"{sien.Bpm,9:F1} BPM{fenetres,7}";
    if (impose is { } p)
        ligne += $"{Pulsation.Accord(t, p),9:+0.000;-0.000}{Pulsation.Rapport(sien.Periode, p.Periode),16}";
    Console.WriteLine(ligne);
}

Console.WriteLine();
Console.WriteLine("force  : concentration des instants sur leur meilleure periode, 0 a 1,");
Console.WriteLine("         mediane sur des fenetres de quinze secondes — une mesure globale");
Console.WriteLine("         exigerait un tempo constant au millieme. « fois » la rapporte au hasard.");
Console.WriteLine("stable : part des fenetres qui trouvent la meme periode a 3 % pres. Un vrai");
Console.WriteLine("         pouls la retrouve partout ; des frappes irregulieres en changent.");
if (contre)
{
    Console.WriteLine("accord : +1 chaque frappe sur un temps de la reference · 0 dispersees");
    Console.WriteLine("         -1 toutes exactement ENTRE deux temps — regulier, et a cote.");
}
return 0;
