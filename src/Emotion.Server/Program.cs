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
                                        separate: cfg.GetValue("Signal:Separate", true)),
        _       => new MockAudioSource(cfg.GetValue("Signal:Bpm", 87f)),
    };

    // Seconde entree facultative : la sortie casque de la table. Sans elle, le systeme
    // fonctionne exactement comme avant et la transition reste commandee a la main.
    var cueDevice = cfg["Signal:CueDevice"];
    if (string.IsNullOrWhiteSpace(cueDevice)) return master;

    return new DualAudioSource(master,
        new PulseAudioSource(cueDevice, separate: cfg.GetValue("Signal:Separate", true)));
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
app.UseStaticFiles();

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
