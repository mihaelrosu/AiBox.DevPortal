namespace AiBox.DevPortal.Models;

public sealed record PatchPreviewRepairContext(
    string OriginalTask,
    string RawModelResponse,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<PatchReplaceDiagnostic> ReplaceDiagnostics,
    IReadOnlyList<PatchSuggestedTargetDiagnostic> SuggestedTargetDiagnostics);
