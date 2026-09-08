using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Emotion.Server;
using Emotion.Signal;

var builder = WebApplication.CreateBuilder(args);

// Les enums partent par leur nom, pas par leur rang. Le renderer teste `Waves` et non
// `1` : un jour ou l'autre on inserera une valeur au milieu de l'enum, et un renderer
// qui compare des entiers changerait alors de phenomene sans que rien ne le signale.
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters
        .Add(new JsonStringEnumConverter()));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters
    .Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<DeckState>();

// CE QUE LE SYSTEME A APPRIS SURVIT A L'ARRET DU SERVEUR.
//
// Un disque passe hier doit etre reconnu ce soir : les portraits de ses sources et son
// tempo sont ranges sur le disque dur, un fichier par morceau. Sans cette persistance, la
// preparation au casque ne servirait que la soiree en cours et chaque set repartirait de
// rien.
builder.Services.AddSingleton(new KnowledgeStore(
    Path.Combine(builder.Environment.ContentRootPath, "connaissance")));
builder.Services.AddSingleton<TrackMemory>();
builder.Services.AddHostedService<MemoryKeeper>();

// Le bus de diffusion : un producteur, plusieurs consommateurs, aucun ne pouvant
// ralentir les autres.
builder.Services.AddSingleton<FrameBus>();

// La source se choisit par configuration. Aujourd'hui il n'y en a qu'une, mais le
// jour ou la table est branchee, seule cette ligne change : ni le worker, ni le hub,
// ni le renderer ne savent d'ou vient le signal.
builder.Services.AddSingleton<IAudioSource>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    // "mock" fabrique un signal a partir d'un tempo, sans carte son.
    // "pulse" ecoute pour de vrai : le monitor de la sortie pour essayer sans
    // materiel, l'entree ligne le jour ou la table est branchee.
    IAudioSource master = cfg["Signal:Source"]?.ToLowerInvariant() switch
    {
        "pulse" => new PulseAudioSource(cfg["Signal:Device"],
                                        separate: cfg.GetValue("Signal:Separate", false)),
        _       => new MockAudioSource(cfg.GetValue("Signal:Bpm", 87f)),
    };

    // Seconde entree facultative : la sortie casque de la table. Sans elle, le systeme
    // fonctionne exactement comme avant et la transition reste commandee a la main.
    var cueDevice = cfg["Signal:CueDevice"];
    if (string.IsNullOrWhiteSpace(cueDevice)) return master;

    return new DualAudioSource(master,
        new PulseAudioSource(cueDevice, separate: cfg.GetValue("Signal:Separate", false)));
});

builder.Services.AddHostedService<SignalWorker>();

// L'unite de rendu externe, s'abonnant au bus comme n'importe quel consommateur.
// Absente, le visuel web tourne exactement pareil.
builder.Services.AddSingleton<GpuSink>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GpuSink>());

// Le renderer est servi par le meme processus que le hub : une seule adresse a ouvrir
// sur la machine du projecteur, et aucune question d'origine croisee.
var app = builder.Build();

app.UseDefaultFiles();

// Le renderer se regle en boucle : on modifie un shader, on recharge, on juge. Or les
// modules ES sont mis en cache aussi agressivement que le reste, et le navigateur
// continue de servir l'ancien code sans rien signaler — on croit alors juger une
// correction qui n'est pas chargee. Ce piege a coute plusieurs allers-retours.
//
// Le cout d'un rechargement complet est nul ici : quelques kilooctets depuis localhost.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        ctx.Context.Response.Headers.Pragma = "no-cache";
    },
});

// Les clips et les images vivent hors du depot : le manifeste est versionne, les
// oeuvres non. Le dossier est donc servi depuis la racine du projet plutot que depuis
// wwwroot, et son absence n'empeche pas le serveur de demarrer.
var assets = Path.Combine(app.Environment.ContentRootPath, "..", "..", "assets");
assets = Path.GetFullPath(assets);
if (Directory.Exists(assets))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(assets),
        RequestPath = "/assets",
        ServeUnknownFileTypes = false,
    });
    app.Logger.LogInformation("Assets servis depuis {Path}", assets);
}
else
{
    app.Logger.LogInformation("Aucun dossier d'assets : le visuel tourne en geometrie seule.");
}

app.MapHub<VisualHub>("/hub");

// Les commandes venues du telephone : caler, basculer, renoncer.
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
