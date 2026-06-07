namespace AiBox.DevPortal.Models.Agents;

public sealed class LocalCoderPatchValidationResult
{
    public bool IsValid { get; set; }
    public IReadOnlyList<string> TouchedPaths { get; set; } = [];
    public IReadOnlyList<string> Errors { get; set; } = [];
}
