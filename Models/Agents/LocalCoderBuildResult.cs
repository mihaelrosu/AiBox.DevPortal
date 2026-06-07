namespace AiBox.DevPortal.Models.Agents;

public sealed class LocalCoderBuildResult
{
    public bool Success { get; set; }
    public int? ExitCode { get; set; }
    public string Command { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}
