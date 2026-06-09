namespace AiBox.DevPortal.Models;

public sealed class LocalCoderPatchApplyResult
{
    public bool Applied { get; set; }

    public bool VerificationPassed { get; set; }

    public string Message { get; set; } = string.Empty;

    public IReadOnlyList<string> ChangedFiles { get; set; } = [];

    public IReadOnlyList<string> BackupFiles { get; set; } = [];

    public IReadOnlyList<CommandRunResult> VerificationResults { get; set; } = [];
}
