namespace AiBox.DevPortal.Models;

public sealed class LocalCoderTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public string ProjectPath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public List<CommandRunResult> Commands { get; set; } = [];
}
