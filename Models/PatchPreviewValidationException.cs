namespace AiBox.DevPortal.Models;

public sealed class PatchPreviewValidationException : InvalidOperationException
{
    public PatchPreviewValidationException(
        string message,
        IReadOnlyList<string> validationErrors,
        string rawModelResponse,
        string normalizedDiff,
        IReadOnlyList<PatchValidationGuidance>? guidance = null)
        : base(message)
    {
        ValidationErrors = validationErrors;
        RawModelResponse = rawModelResponse;
        NormalizedDiff = normalizedDiff;
        Guidance = guidance ?? PatchValidationGuidanceFactory.Create(validationErrors);
    }

    public IReadOnlyList<string> ValidationErrors { get; }

    public string RawModelResponse { get; }

    public string NormalizedDiff { get; }

    public IReadOnlyList<PatchValidationGuidance> Guidance { get; }
}
