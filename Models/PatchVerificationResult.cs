namespace AiBox.DevPortal.Models;

public sealed class PatchVerificationResult
{
    public string ProjectPath { get; set; } = string.Empty;

    public IReadOnlyList<PatchVerificationCommandResult> Commands { get; set; } = [];

    public bool Passed => Commands.Count > 0 && Commands.All(command => command.Passed);

    public string Summary { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public DateTimeOffset VerifiedAt { get; set; } = DateTimeOffset.UtcNow;
}
