namespace AiBox.DevPortal.Models;

public sealed class PatchPreviewRepairResult
{
    public bool Success { get; set; }
    public PatchEditOperationResult? EditResult { get; set; }
    public PatchPreviewRepairSummary? RepairSummary { get; set; }
    public PatchPreviewValidationException? OriginalValidationException { get; set; }
    public PatchPreviewValidationException? RepairValidationException { get; set; }
    public PatchPreviewValidationException? ValidationException { get; set; }
    public string RepairPrompt { get; set; } = string.Empty;
    public string OriginalRawResponse { get; set; } = string.Empty;
    public string OriginalNormalizedResponse { get; set; } = string.Empty;
    public string RepairRawResponse { get; set; } = string.Empty;
    public string RepairNormalizedResponse { get; set; } = string.Empty;
    public IReadOnlyList<string> CombinedValidationErrors { get; set; } = [];
    public string FailureMessage { get; set; } = string.Empty;
}
