namespace AiBox.DevPortal.Models;

public sealed class LocalCoderPatchPreview
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public string ProjectPath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public IReadOnlyList<LocalCoderFileContext> FileContexts { get; set; } = [];
    public IReadOnlyList<PatchFileChange> FileChanges { get; set; } = [];
    public PatchScopeMode AllowedPatchScope { get; set; } = PatchScopeMode.ContextFilesOnly;
    public IReadOnlyList<string> AllowedPatchFolders { get; set; } = [];
    public IReadOnlyList<string> AllowedCreateFolders { get; set; } = [];
    public PatchScopeAnalysis ScopeAnalysis { get; set; } = new();
    public PatchIntent? Intent { get; set; }
    public PatchIntentValidation? IntentValidation { get; set; }
    public PatchContextCoverage ContextCoverage { get; set; } = new();
    public PatchPromptAudit? PromptAudit { get; set; }
    public PatchPromptTargetResolution? PromptTargetResolution { get; set; }
    public PatchPreviewRepairSummary? RepairSummary { get; set; }
    public bool RequiresApproval { get; set; }
    public string ApprovalReason { get; set; } = string.Empty;
    public DateTimeOffset? ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public IReadOnlyList<string> VerificationCommands { get; set; } = [];
    public string PatchText { get; set; } = string.Empty;
}
