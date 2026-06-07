namespace AiBox.DevPortal.Models;

public sealed class CommandRunResult
{
    public string Command { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Finished { get; set; } = DateTimeOffset.UtcNow;
}
