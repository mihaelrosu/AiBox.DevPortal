namespace AiBox.DevPortal.Models;

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

    public LocalCoderPatchApplyResult? ApplyResult { get; set; }

    public LocalCoderPatchRollbackResult? RollbackResult { get; set; }

    public IReadOnlyList<CommandRunResult> VerificationResults { get; set; } = [];

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}
