namespace AiBox.DevPortal.Models;

public sealed class PatchEditOperationResult
{
    public IReadOnlyList<PatchEditOperation> Operations { get; set; } = [];
    public IReadOnlyList<PatchFileChange> FileChanges { get; set; } = [];
    public string PatchText { get; set; } = string.Empty;
}
