namespace AiBox.DevPortal.Models;

public sealed class LocalCoderPatchRollbackResult
{
    public bool RolledBack { get; set; }

    public bool VerificationPassed { get; set; }

    public string Message { get; set; } = string.Empty;

    public IReadOnlyList<string> RestoredFiles { get; set; } = [];

    public IReadOnlyList<CommandRunResult> VerificationResults { get; set; } = [];
}
