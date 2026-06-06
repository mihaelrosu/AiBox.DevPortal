namespace AiBox.DevPortal.Models;

public sealed class DockerContainerInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Ports { get; set; } = string.Empty;
    public DateTime Created { get; set; }
}
