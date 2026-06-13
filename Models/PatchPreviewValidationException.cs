namespace AiBox.DevPortal.Models;

public sealed class PatchPreviewValidationException : InvalidOperationException
{
    public PatchPreviewValidationException(
        string message,
        IReadOnlyList<string> validationErrors,
        string rawModelResponse,
        string normalizedDiff,
        IReadOnlyList<string>? operationGrammarErrors = null,
        IReadOnlyList<PatchValidationGuidance>? guidance = null,
        IReadOnlyList<PatchReplaceDiagnostic>? replaceDiagnostics = null,
        IReadOnlyList<PatchSuggestedTargetDiagnostic>? suggestedTargetDiagnostics = null)
        : base(message)
    {
        ValidationErrors = validationErrors;
        RawModelResponse = rawModelResponse;
        NormalizedDiff = normalizedDiff;
        OperationGrammarErrors = operationGrammarErrors ?? [];
        Guidance = guidance ?? PatchValidationGuidanceFactory.Create(validationErrors);
        ReplaceDiagnostics = replaceDiagnostics ?? [];
        SuggestedTargetDiagnostics = suggestedTargetDiagnostics ?? [];
    }

    public PatchPreviewValidationException(
        string message,
        IReadOnlyList<string> validationErrors,
        string rawModelResponse,
        string normalizedDiff,
        string normalizedResponse,
        IReadOnlyList<string>? operationGrammarErrors = null,
        IReadOnlyList<PatchValidationGuidance>? guidance = null,
        IReadOnlyList<PatchReplaceDiagnostic>? replaceDiagnostics = null,
        IReadOnlyList<PatchSuggestedTargetDiagnostic>? suggestedTargetDiagnostics = null)
        : this(message, validationErrors, rawModelResponse, normalizedDiff, operationGrammarErrors, guidance, replaceDiagnostics, suggestedTargetDiagnostics)
    {
        NormalizedResponse = normalizedResponse;
    }

    public IReadOnlyList<string> ValidationErrors { get; }

    public string RawModelResponse { get; }

    public string NormalizedDiff { get; }

    public string NormalizedResponse { get; } = string.Empty;

    public IReadOnlyList<string> OperationGrammarErrors { get; }

    public IReadOnlyList<PatchValidationGuidance> Guidance { get; }

    public IReadOnlyList<PatchReplaceDiagnostic> ReplaceDiagnostics { get; }

    public IReadOnlyList<PatchSuggestedTargetDiagnostic> SuggestedTargetDiagnostics { get; }

    public PatchPreviewRepairSummary? RepairSummary { get; set; }
}
