namespace AiBox.DevPortal.Models;

/// <summary>
/// Represents one saved Local Coder history entry.
/// </summary>
public sealed class LocalCoderHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public LocalCoderHistoryActionType ActionType { get; set; }

    public string ProjectPath { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Task { get; set; } = string.Empty;

    public IReadOnlyList<string> SelectedFiles { get; set; } = [];

    public IReadOnlyList<LocalCoderHistoryFileSummary> LoadedContextFiles { get; set; } = [];

    public int ContextFileCount { get; set; }

    public int ContextTotalCharacters { get; set; }

    public int ContextEstimatedTokens { get; set; }

    public IReadOnlyList<LocalCoderHistoryContextFile> ContextFiles { get; set; } = [];

    public IReadOnlyList<LocalCoderPlanTask> PlanTasks { get; set; } = [];

    public string PlanText { get; set; } = string.Empty;

    public string PatchPreviewText { get; set; } = string.Empty;

    public PatchScopeAnalysis? ScopeAnalysis { get; set; }

    public IReadOnlyList<string> AllowedCreateFolders { get; set; } = [];

    public PatchIntent? Intent { get; set; }

    public PatchIntentValidation? IntentValidation { get; set; }

    public PatchContextCoverage? ContextCoverage { get; set; }

    public PatchPromptAudit? PromptAudit { get; set; }

    public PatchPromptTargetResolution? PromptTargetResolution { get; set; }

    public IReadOnlyList<string> ValidationErrors { get; set; } = [];

    public IReadOnlyList<string> OperationGrammarErrors { get; set; } = [];

    public IReadOnlyList<PatchValidationGuidance> ValidationGuidance { get; set; } = [];

    public IReadOnlyList<PatchReplaceDiagnostic> ReplaceDiagnostics { get; set; } = [];

    public IReadOnlyList<PatchSuggestedTargetDiagnostic> SuggestedTargetDiagnostics { get; set; } = [];

    public PatchPreviewRepairSummary? RepairSummary { get; set; }

    public LocalCoderPatchApplyResult? ApplyResult { get; set; }

    public LocalCoderPatchRollbackResult? RollbackResult { get; set; }

    public IReadOnlyList<CommandRunResult> VerificationResults { get; set; } = [];

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}
