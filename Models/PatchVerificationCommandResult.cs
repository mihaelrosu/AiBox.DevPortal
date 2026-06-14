namespace AiBox.DevPortal.Models;

public sealed class PatchVerificationCommandResult
{
    public string Command { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public string Output { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Passed => ExitCode == 0;
}
