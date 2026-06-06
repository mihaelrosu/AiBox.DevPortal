namespace AiBox.DevPortal.Models;

public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string Status,
    string Ports);
