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
    public string PatchText { get; set; } = string.Empty;
}
