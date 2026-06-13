namespace AiBox.DevPortal.Models;

public sealed class PatchPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string BackupFolder { get; set; } = string.Empty;
    public DateTimeOffset? AppliedAt { get; set; }
    public bool RolledBack { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public string UserRequest { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PatchText { get; set; } = string.Empty;
    public PatchScopeMode? AllowedPatchScope { get; set; }
    public IReadOnlyList<string> AllowedPatchFolders { get; set; } = [];
    public IReadOnlyList<string> AllowedCreateFolders { get; set; } = [];
    public IReadOnlyList<string> ContextFilePaths { get; set; } = [];
    public PatchIntent? Intent { get; set; }
    public PatchIntentValidation? IntentValidation { get; set; }
    public PatchPackageStatus Status { get; set; } = PatchPackageStatus.Draft;
    public string StatusMessage { get; set; } = string.Empty;
    public IReadOnlyList<PatchApprovalGateResult> ApprovalGateResults { get; set; } = [];
    public bool ApprovalGatesPassed =>
        ApprovalGateResults is null ||
        ApprovalGateResults.Count == 0 ||
        ApprovalGateResults.All(result => result.Passed || !result.Blocking);
    public string RollbackResult { get; set; } = string.Empty;
    public string RollbackError { get; set; } = string.Empty;
    public IReadOnlyList<PatchFileChange> FileChanges { get; set; } = [];
}
