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

builder.Services.AddSingleton<TrackState>();

// La source se choisit par configuration. Aujourd'hui il n'y en a qu'une, mais le
// jour ou la table est branchee, seule cette ligne change : ni le worker, ni le hub,
// ni le renderer ne savent d'ou vient le signal.
builder.Services.AddSingleton<IAudioSource>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var bpm = cfg.GetValue("Signal:Bpm", 87f);
    return new MockAudioSource(bpm);
});

builder.Services.AddHostedService<SignalWorker>();

// Le renderer est servi par le meme processus que le hub : une seule adresse a ouvrir
// sur la machine du projecteur, et aucune question d'origine croisee.
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<VisualHub>("/hub");

// Crate annonce la face posee. Un POST plutot qu'une methode de hub : la PWA n'a
// alors aucun client SignalR a embarquer, un fetch suffit, et le meme appel se teste
// en une ligne de curl depuis les platines.
app.MapPost("/track", async (TrackContext track, TrackState state,
                             IHubContext<VisualHub> hub) =>
{
    state.Current = track;
    await hub.Clients.All.SendAsync("track", track);
    return Results.NoContent();
});

// Ce que le renderer affiche en ce moment, pour verifier l'etat sans ouvrir la page.
app.MapGet("/track", (TrackState state) => Results.Ok(state.Current));

app.Run();
