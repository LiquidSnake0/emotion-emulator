using Emotion.Signal;
using Microsoft.AspNetCore.SignalR;

namespace Emotion.Server;

/// <summary>
/// Ce qui pousse les images vers un client distant, s'il y en a un.
///
/// POURQUOI CETTE INTERFACE EXISTE.
///
/// Le moteur etait un serveur web, et il ne l'etait que pour le navigateur. L'analyse, elle,
/// n'a jamais eu besoin d'un port : elle capture le son, l'analyse, et publie 256 octets dans
/// /dev/shm. L'unite de rendu lira ces octets sur PCIe ou USB-C, pas sur une socket.
///
/// La boucle d'analyse ne connait donc plus SignalR, seulement cette interface. Elle peut
/// tourner avec <see cref="DiffusionMuette"/>, sans hote web, sans port ouvert, sans rien
/// negocier — et c'est ainsi qu'elle tournera le jour ou le navigateur aura disparu.
/// </summary>
public interface IDiffusion
{
    /// <summary>Une image d'analyse, a cadence pleine.</summary>
    Task Image(VisualFrame frame, CancellationToken ct);

    /// <summary>L'etat du casque, cinq fois par seconde : un bandeau, pas une animation.</summary>
    Task Casque(VisualFrame frame, CancellationToken ct);

    /// <summary>Le feu vert, une fois et une seule par disque.</summary>
    Task Pret(Readiness readiness, CancellationToken ct);
}

/// <summary>
/// Diffusion vers le navigateur. Ne subsiste que tant qu'un navigateur regarde.
/// </summary>
public sealed class DiffusionSignalR(IHubContext<VisualHub> hub) : IDiffusion
{
    public Task Image(VisualFrame frame, CancellationToken ct) =>
        hub.Clients.All.SendAsync("frame", frame, ct);

    public Task Casque(VisualFrame frame, CancellationToken ct) =>
        hub.Clients.All.SendAsync("cue", frame, ct);

    public Task Pret(Readiness readiness, CancellationToken ct) =>
        hub.Clients.All.SendAsync("ready", readiness, ct);
}

/// <summary>
/// Aucune diffusion. Le moteur capture, analyse et publie dans l'anneau ; personne n'ecoute
/// par le reseau, et c'est le cas nominal.
/// </summary>
public sealed class DiffusionMuette : IDiffusion
{
    public Task Image(VisualFrame frame, CancellationToken ct) => Task.CompletedTask;
    public Task Casque(VisualFrame frame, CancellationToken ct) => Task.CompletedTask;
    public Task Pret(Readiness readiness, CancellationToken ct) => Task.CompletedTask;
}
