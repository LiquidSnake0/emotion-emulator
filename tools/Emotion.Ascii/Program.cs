using System.Text;
using Emotion.Signal;

// Renderer natif, dans le terminal, lisant directement l'anneau partage.
//
// POURQUOI IL EXISTE.
//
// Le visuel etait juge a travers un navigateur, et le navigateur n'est pas la cible. Le
// jour ou l'unite de rendu tournera, elle lira ces memes 256 octets dans /dev/shm sans
// qu'aucun HTTP, aucun WebSocket ni aucun moteur de rendu web ne s'interpose. Juger la
// fluidite a travers Chrome, c'est mesurer un chemin qu'on ne livrera jamais.
//
// Celui-ci ne demande rien a personne : il ouvre le fichier partage, lit le dernier paquet
// publie, dessine. Pas de dependance, pas de pilote graphique, pas de negociation. C'est
// aussi la premiere validation reelle du contrat GPU — si ce programme affiche quelque
// chose de juste, le contrat tient.
//
//   dotnet run -c Release --project tools/Emotion.Ascii
//
// « q » pour sortir.

const string Esc = "[";
const string Reset = Esc + "0m";
const string Home = Esc + "H";
const string Efface = Esc + "2J";
const string FinLigne = Esc + "K";
const string CacheCurseur = Esc + "?25l";
const string MontreCurseur = Esc + "?25h";

// LA CHARTE : du gris, un seul accent vert, aucun rouge. Un renderer de diagnostic qui
// invente ses couleurs ne montre pas le meme objet que le renderer de scene.
const string Vert = Esc + "38;5;42m";
const string VertPale = Esc + "38;5;29m";
const string Blanc = Esc + "38;5;252m";

var chemin = args.Length > 0 ? args[0] : SharedRing.DefaultPath;

if (!File.Exists(chemin))
{
    Console.Error.WriteLine($"anneau introuvable : {chemin}");
    Console.Error.WriteLine("lancer le serveur d'abord — ./run.sh pulse");
    return 1;
}

using var lecteur = new SharedRingReader(chemin);
Console.Write(Efface + CacheCurseur);

var tampon = new StringBuilder(16 * 1024);
var dernier = default(GpuPacket);
long vus = 0, images = 0;
var t0 = DateTime.UtcNow;

// Enveloppes locales : le paquet porte des impulsions, pas des etats. Les faire decroitre
// est le travail du renderer — c'est la meme regle que dans le renderer web, et la meme
// raison : une impulsion qui resterait vraie figerait l'ecran au maximum.
float pulseKick = 0, pulseClap = 0, pulseHat = 0;
var pulseSource = new float[6];
var dernierPaquet = -1L;

try
{
    while (true)
    {
        // Console.KeyAvailable leve quand l'entree est redirigee — ce qui arrive des
        // qu'on canalise la sortie pour l'examiner. Un outil de diagnostic ne doit pas
        // tomber pour si peu.
        if (!Console.IsInputRedirected && Console.KeyAvailable
            && Console.ReadKey(true).KeyChar is 'q' or 'Q') break;

        // On vide l'anneau jusqu'au plus recent : une image en retard n'a aucune valeur,
        // c'est la regle du projet et elle vaut aussi a la lecture.
        while (lecteur.TryRead(out var p)) { dernier = p; vus++; }

        // Les impulsions ne se consomment qu'une fois par paquet, jamais par image de
        // rendu. Le renderer web s'y etait fait prendre : il redessinait le dernier paquet
        // a chaque reveil et relisait les memes drapeaux, ce qui epinglait le battement.
        if (dernier.Sequence != dernierPaquet)
        {
            dernierPaquet = dernier.Sequence;
            var h = dernier.Hits;
            if ((h & 1) != 0) pulseKick = 1f;
            if ((h & 2) != 0) pulseClap = 1f;
            if ((h & 4) != 0) pulseHat = 1f;
            for (var r = 0; r < 6; r++)
                if (dernier.ReadSource(r).Hit) pulseSource[r] = 1f;
        }

        const float dt = 1f / 60f;
        pulseKick = MathF.Max(0f, pulseKick - dt * 3.2f);
        pulseClap = MathF.Max(0f, pulseClap - dt * 4.5f);
        pulseHat = MathF.Max(0f, pulseHat - dt * 7f);
        for (var r = 0; r < 6; r++) pulseSource[r] = MathF.Max(0f, pulseSource[r] - dt * 4f);

        images++;
        Dessine(tampon, dernier, pulseKick, pulseClap, pulseHat, pulseSource,
                vus, lecteur.Missed, images, (DateTime.UtcNow - t0).TotalSeconds);

        Console.Out.Write(tampon);
        Console.Out.Flush();
        Thread.Sleep(16);
    }
}
finally
{
    Console.Write(MontreCurseur + Reset + "\n");
}
return 0;

static string Gris(int n) => Esc + $"38;5;{232 + Math.Clamp(n, 0, 23)}m";

static void Dessine(StringBuilder b, in GpuPacket p, float kick, float clap, float hat,
                    float[] src, long vus, long manques, long images, double secondes)
{
    b.Clear();
    b.Append(Home);

    var largeur = Math.Max(64, Math.Min(SafeWidth(), 150));

    // ---- bandeau ----
    b.Append(Blanc).Append("  emotion   ").Append(Vert)
     .Append(p.Bpm > 0 ? $"{p.Bpm,6:F1}" : "     —").Append(" BPM").Append(Reset)
     .Append(Gris(11))
     .Append($"   paquet {p.Sequence}   lus {vus}   perdus {manques}   ")
     .Append($"{images / Math.Max(0.001, secondes),5:F1} img/s")
     .Append(Reset).Append(FinLigne).Append('\n');

    // ---- la mesure ----
    // La position vient du paquet, pas d'une horloge locale : c'est ce qu'on veut verifier.
    const int Cases = 32;
    var ou = (int)(Math.Clamp(p.Phase, 0f, 0.999f) * Cases);
    b.Append(Gris(9)).Append("  mesure    ").Append(Reset);
    for (var i = 0; i < Cases; i++)
    {
        var fort = i % 8 == 0;
        if (i == ou) b.Append(Vert).Append('#').Append(Reset);
        else b.Append(fort ? Gris(15) : Gris(6)).Append(fort ? '|' : '.').Append(Reset);
    }
    b.Append(Gris(11)).Append("   temps ").Append(Blanc)
     .Append(p.Beat < 4 ? (p.Beat + 1).ToString() : "-").Append(Reset)
     .Append(FinLigne).Append('\n').Append(FinLigne).Append('\n');

    // ---- les six sources ----
    var utile = Math.Max(20, largeur - 26);
    for (var r = 0; r < 6; r++)
    {
        var s = p.ReadSource(r);
        var niveau = s.Level / 255f;
        var hauteur = s.Pitch / 255f;
        var nette = s.Sharp / 255f;

        b.Append(Gris(11)).Append($"  source {r + 1}  ").Append(Reset);

        // Deux grandeurs, deux dessins : la barre porte le niveau, le curseur porte la
        // hauteur dans l'etendue que la source parcourt.
        var n = (int)(niveau * utile);
        var curseur = (int)(hauteur * (utile - 1));
        for (var i = 0; i < utile; i++)
        {
            if (i == curseur) b.Append(src[r] > 0.05f ? Vert : Blanc).Append('#').Append(Reset);
            else if (i < n) b.Append(VertPale).Append('=').Append(Reset);
            else b.Append(Gris(4)).Append('.').Append(Reset);
        }
        b.Append(nette > 0.6f ? Gris(15) : Gris(7))
         .Append(nette > 0.6f ? "  nette" : "  ....").Append(Reset)
         .Append(FinLigne).Append('\n');
    }
    b.Append(FinLigne).Append('\n');

    // ---- les douze bandes ----
    b.Append(Gris(11)).Append("  spectre   ").Append(Reset);
    for (var i = 0; i < 12; i++)
    {
        var v = p.Bands[i] / 255f;
        b.Append(v > 0.66f ? Vert : v > 0.33f ? VertPale : Gris(8))
         .Append(v > 0.85f ? '#' : v > 0.6f ? '+' : v > 0.3f ? '-' : '.')
         .Append(Reset).Append(' ');
    }
    b.Append(FinLigne).Append('\n').Append(FinLigne).Append('\n');

    // ---- les frappes ----
    Barre(b, "kick", kick);
    Barre(b, "clap", clap);
    Barre(b, "charley", hat);

    b.Append(FinLigne).Append('\n').Append(Gris(8))
     .Append("  q pour sortir").Append(Reset).Append(FinLigne).Append('\n');
}

static void Barre(StringBuilder b, string nom, float v)
{
    b.Append(Gris(11)).Append($"  {nom,-10}").Append(Reset);
    var n = (int)(v * 40);
    for (var i = 0; i < 40; i++)
        b.Append(i < n ? Vert + "=" : Gris(4) + ".").Append(Reset);
    b.Append(FinLigne).Append('\n');
}

// La largeur n'est pas toujours lisible — sortie redirigee, terminal absent. On ne veut pas
// qu'un outil de diagnostic tombe pour si peu.
static int SafeWidth()
{
    try { return Console.WindowWidth - 1; }
    catch { return 100; }
}
