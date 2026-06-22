namespace AiBox.DevPortal.Models;

public sealed class LocalCoderPatchApplyResult
{
    public bool Applied { get; set; }

    public bool VerificationPassed { get; set; }

    public string Message { get; set; } = string.Empty;

    public string TechnicalDetails { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }
    public string ApprovalReason { get; set; } = string.Empty;
    public DateTimeOffset? ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public CommandRunResult VerificationRun { get; set; } = new()
    {
        Command = "dotnet build (not run)",
        ExitCode = -1
    };
    public bool VerificationSucceeded { get; set; }
    public string VerificationOutput { get; set; } = string.Empty;
    public bool RestoreAttempted { get; set; }
    public bool RestoreSucceeded { get; set; }
    public string RestoreMessage { get; set; } = string.Empty;

    public CommandRunResult GitApplyResult { get; set; } = new();

    public CommandRunResult GitDiffStatResult { get; set; } = new()
    {
        Command = "git diff --stat (not run)",
        ExitCode = -1
    };

    public CommandRunResult ChangedFilesDiffResult { get; set; } = new()
    {
        Command = "git diff -- <changed files> (not run)",
        ExitCode = -1
    };

    public IReadOnlyList<string> ChangedFiles { get; set; } = [];

    public IReadOnlyList<string> BackupFiles { get; set; } = [];

    public IReadOnlyList<CommandRunResult> VerificationResults { get; set; } = [];
}
