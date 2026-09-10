using System.Text.Json.Serialization;
using Emotion.Server;
using Emotion.Signal;

// SANS RESEAU, ET C'EST LE CAS NOMINAL.
//
// Le moteur a ete un serveur web, et il ne l'etait que pour le navigateur. L'analyse, elle,
// n'a jamais eu besoin d'un port : elle capture le son, l'analyse, et publie 256 octets dans
// /dev/shm. L'unite de rendu lit ces octets, et les lira sur PCIe ou USB-C avec un eGPU —
// jamais sur une socket. Les fenetres de reglage les lisent deja de la meme facon.
//
//   dotnet run --project src/Emotion.Server -- --sans-reseau
//
// Rien n'est ouvert, rien n'est negocie, rien ne peut echouer faute de port libre.
//
// LE CHEMIN WEB N'A PLUS QU'UNE RAISON D'ETRE : le crate appelle /deck et /ready en HTTP,
// et c'est le seul reseau legitime du systeme. Il ne sert plus une seule page.
if (args.Contains("--sans-reseau"))
{
    var moteur = Host.CreateApplicationBuilder(args);
    moteur.Services.AddSingleton<DeckState>();
    moteur.Services.AddSingleton<TrackMemory>();
    moteur.Services.AddSingleton<FrameBus>();
    moteur.Services.AddSingleton<IAudioSource>(SourceDuSignal);
    moteur.Services.AddSingleton<GpuSink>();
    moteur.Services.AddHostedService(sp => sp.GetRequiredService<GpuSink>());
    moteur.Services.AddHostedService<SignalWorker>();
    moteur.Build().Run();
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Les enums partent par leur nom, pas par leur rang. Le crate lit `Waves` et non `1` : un
// jour ou l'autre on inserera une valeur au milieu de l'enum, et un client qui compare des
// entiers changerait alors de phenomene sans que rien ne le signale.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters
    .Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<DeckState>();

// CE QUE LE SYSTEME APPREND VIT EN MEMOIRE, ET S'ARRETE AVEC LE DISQUE.
//
// Une premiere version rangeait les portraits sur le disque dur, un fichier par face
// toutes les dix secondes. La mesure a tranche : le cout median d'une image passait de 2,0
// a 3,2 ms et le pire cas de 17 a 34 ms, soit au-dessus du pas de 21 ms. Reconnaitre un
// disque la semaine prochaine ne vaut pas d'alourdir la soiree en cours.
builder.Services.AddSingleton<TrackMemory>();

// Le bus de diffusion : un producteur, plusieurs consommateurs, aucun ne pouvant
// ralentir les autres.
builder.Services.AddSingleton<FrameBus>();

// La source se choisit par configuration. Aujourd'hui il n'y en a qu'une, mais le
// jour ou la table est branchee, seule cette ligne change : ni le worker ni les endpoints
// ne savent d'ou vient le signal.
builder.Services.AddSingleton<IAudioSource>(SourceDuSignal);

builder.Services.AddHostedService<SignalWorker>();

// L'unite de rendu, s'abonnant au bus comme n'importe quel consommateur. C'est par elle
// que passe desormais TOUT le rendu : elle ecrit dans l'anneau partage, et ce qui affiche
// le lit — la fenetre Qt aujourd'hui, l'eGPU demain, par le meme contrat de 256 octets.
builder.Services.AddSingleton<GpuSink>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GpuSink>());

// CE PROCESSUS N'EST PLUS UN SERVEUR DE PAGES.
//
// Il n'y a plus ni fichier statique, ni hub, ni dossier d'assets a servir : le rendu ne
// passe plus par le reseau du tout. Ce qui reste ouvert est l'API que le crate appelle,
// et rien d'autre.
var app = builder.Build();

// Les commandes venues du crate et du telephone : caler, basculer, renoncer.
app.MapDeck();

// Etat des tuyaux : ce qui a ete livre, ce qui a ete jete, et le temps d'ecriture dans
// l'anneau. Sans cette page, un visuel qui saccade reste indebuggable.
// L'etat de preparation, interrogeable depuis le telephone. Le meme contenu part aussi
// en notification par le hub, mais un endpoint permet de le lire a tout moment — par
// exemple pour afficher une jauge en rouvrant l'application.
app.MapGet("/ready", (FrameBus bus) =>
{
    var f = bus.Latest;
    var r = f.Readiness;
    return Results.Ok(new
    {
        pret = r.Ready,
        motif = r.Reason,
        avancement = r.Progress,
        secondesRestantes = r.SecondsLeft,
        // Ce a quoi le renderer aura le droit de se fier, et dans quelle mesure.
        tempo = f.Bpm,
        confianceTempsFort = f.Structure.Confidence,
        confianceStructure = f.Structure.SectionConfidence,
        fiabilite = f.Structure.Trust,
    });
});

// L'IMAGE COURANTE, TELLE QUE LE HUB LA SERIALISE.
//
// Diagnostic d'un defaut precis : la diffusion vers le navigateur mourait silencieusement
// apres une quarantaine de secondes, sans erreur nulle part, pendant que la boucle serveur
// continuait de publier a plein regime — douze mille images pour un navigateur fige depuis
// quarante-cinq secondes.
//
// System.Text.Json leve sur NaN et Infinity. Une seule valeur non finie dans l'image suffit
// donc a faire echouer la serialisation, ce qui tue la connexion de ce client-la sans que
// la boucle en sache rien. Cet endpoint emprunte exactement le meme chemin : s'il rend une
// erreur, la cause est trouvee ; s'il rend l'image, il faut chercher ailleurs.
app.MapGet("/image", (FrameBus bus) => Results.Ok(bus.Latest));

// Les valeurs non finies de l'image courante, nommees. Repond a « laquelle ».
app.MapGet("/image/verif", (FrameBus bus) =>
{
    var f = bus.Latest;
    var fautives = new List<string>();

    void V(string nom, float x)
    {
        if (float.IsNaN(x) || float.IsInfinity(x)) fautives.Add($"{nom} = {x}");
    }

    V("Rms", f.Rms);
    V("Novelty", f.Novelty);
    V("Flux", f.Flux);
    V("Threshold", f.Threshold);
    V("Blend", f.Blend);
    V("TempoDrift", f.TempoDrift);
    V("DriftVisible", f.DriftVisible);
    V("AnnouncedBpm", f.AnnouncedBpm);
    V("GridAgreement", f.GridAgreement);
    if (f.Phase is { } ph) V("Phase", ph);
    if (f.Bpm is { } bp) V("Bpm", bp);
    if (f.Bands is { } bandes)
        for (var i = 0; i < bandes.Length; i++) V($"Bands[{i}]", bandes[i]);
    for (var i = 0; i < Voices.Registers; i++)
    {
        var l = f.Voices.LaneAt(i);
        V($"Voices[{i}].Level", l.Level);
        V($"Voices[{i}].Position", l.Position);
        V($"Voices[{i}].Heard", l.Heard);
        V($"Voices[{i}].Sharpness", l.Sharpness);
        V($"Voices[{i}].Brightness", l.Brightness);
        V($"Voices[{i}].Texture", l.Texture);
    }
    V("Timbre.Centroid", f.Timbre.Centroid);
    V("Timbre.Openness", f.Timbre.Openness);
    V("Timbre.Density", f.Timbre.Density);
    V("Structure.PhrasePos", f.Structure.PhrasePos);
    V("Structure.Confidence", f.Structure.Confidence);
    V("Structure.Buildup", f.Structure.Buildup);
    V("Structure.Trust", f.Structure.Trust);
    V("Harmony.Change", f.Harmony.Change);
    V("EventPrint.Brillance", f.EventPrint.Brillance);
    V("EventPrint.Etalement", f.EventPrint.Etalement);
    V("EventPrint.Piquant", f.EventPrint.Piquant);

    return Results.Ok(new { t = f.T, sain = fautives.Count == 0, fautives });
});

app.MapGet("/health", (FrameBus bus, GpuSink gpu) =>
{
    var (written, mean, worst, over100, over1ms) = gpu.Stats();
    return Results.Ok(new
    {
        abonnes = bus.Stats().Select(s => new { s.Name, perdues = s.Dropped }),
        gpu = new
        {
            messages = written,
            ecritureMoyenneUs = Math.Round(mean, 2),
            ecriturePireUs = Math.Round(worst, 2),
            depassements100us = over100,
            depassements1ms = over1ms,
        },
    });
});

app.Run();


// LA FABRIQUE DE LA SOURCE, PARTAGEE PAR LES DEUX CHEMINS.
//
// Elle etait une lambda dans l'enregistrement du service, donc inaccessible au mode sans
// reseau. Une fonction nommee : les deux hotes construisent la meme source, et il n'y a
// aucun risque qu'ils divergent un jour sans qu'on s'en apercoive.
static IAudioSource SourceDuSignal(IServiceProvider sp)
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    // LE JOURNAL DE LA SOURCE N'ETAIT BRANCHE SUR RIEN, ET CELA A COUTE UNE MESURE.
    //
    // PulseAudioSource accepte une action de journalisation et s'en sert pour dire ce que
    // parec ecrit sur sa sortie d'erreur, quand il s'arrete, et quand une fenetre met trop
    // longtemps a venir. Elle n'etait pas passee : `_log` restait nul et tous ces messages
    // partaient au neant. En cherchant d'ou venait un trou de deux secondes, l'absence de
    // ligne « capture lente » a d'abord ete lue comme une preuve que la capture allait
    // bien. Elle ne prouvait rien du tout.
    var journal = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Signal.Capture");
    void Dire(string m) => journal.LogWarning("{Message}", m);

    // "mock" fabrique un signal a partir d'un tempo, sans carte son.
    // "pulse" ecoute pour de vrai : le monitor de la sortie pour essayer sans
    // materiel, l'entree ligne le jour ou la table est branchee.
    IAudioSource master = cfg["Signal:Source"]?.ToLowerInvariant() switch
    {
        "pulse" => new PulseAudioSource(cfg["Signal:Device"],
                                        separate: cfg.GetValue("Signal:Separate", false),
                                        log: Dire),
        // « fichier » rejoue un enregistrement AU RYTHME REEL, dans toute la chaine. Ce
        // n'est pas la sonde : celle-ci court-circuite le serveur et avale les fenetres
        // aussi vite qu'elle peut. Ici tout est identique au direct — cadence, horodatage
        // a l'horloge murale, hub, anneau — sauf qu'aucune fenetre ne peut manquer.
        // C'est la seule facon de separer « le son arrive mal » de « le moteur le traite
        // mal ».
        "fichier" => new WavAudioSource(cfg["Signal:Device"] ?? "",
                                        separate: cfg.GetValue("Signal:Separate", false)),
        _       => new MockAudioSource(cfg.GetValue("Signal:Bpm", 87f)),
    };

    // LA FICHE, QUAND ON REJOUE UN FICHIER POUR REGARDER. En direct elle vient du crate,
    // posee par le selecteur de faces ; en rejeu il n'y a pas de crate au bout, et sans
    // amorce le moteur cherche son tempo dans le vide — 43 % de justesse au lieu de 99.
    // On regarderait alors l'autre systeme, celui qui se trompe, en croyant regarder
    // celui-ci.
    if (master is IAcceptsCue amorcable && cfg.GetValue("Signal:Bpm", 0f) is var bpm and > 0f
        && cfg["Signal:Source"]?.ToLowerInvariant() == "fichier")
    {
        amorcable.Amorcer(bpm);
        Dire($"fiche : {bpm:0.##} BPM");
    }

    // Seconde entree facultative : la sortie casque de la table. Sans elle, le systeme
    // fonctionne exactement comme avant et la transition reste commandee a la main.
    var cueDevice = cfg["Signal:CueDevice"];
    if (string.IsNullOrWhiteSpace(cueDevice)) return master;

    return new DualAudioSource(master,
        new PulseAudioSource(cueDevice, separate: cfg.GetValue("Signal:Separate", false),
                             log: Dire));
}
