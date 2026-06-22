namespace AiBox.DevPortal.Models.Agents;

public sealed class PatchSafetySnapshotRestoreResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> RestoredFiles { get; set; } = [];
    public IReadOnlyList<string> SkippedFiles { get; set; } = [];
}
