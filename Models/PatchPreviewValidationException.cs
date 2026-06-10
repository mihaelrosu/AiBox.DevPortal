namespace AiBox.DevPortal.Models;

public sealed class PatchPreviewValidationException : InvalidOperationException
{
    public PatchPreviewValidationException(
        string message,
        IReadOnlyList<string> validationErrors,
        string rawModelResponse,
        string normalizedDiff)
        : base(message)
    {
        ValidationErrors = validationErrors;
        RawModelResponse = rawModelResponse;
        NormalizedDiff = normalizedDiff;
    }

    public IReadOnlyList<string> ValidationErrors { get; }

    public string RawModelResponse { get; }

    public string NormalizedDiff { get; }
}
