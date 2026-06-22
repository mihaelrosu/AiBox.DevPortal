namespace AiBox.DevPortal.Models.Agents;

public sealed class PatchSafetySnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string PatchPreviewId { get; set; } = string.Empty;
    public string GitStatus { get; set; } = string.Empty;
    public IReadOnlyList<string> TargetFiles { get; set; } = [];
    public IReadOnlyList<string> MissingTargetFiles { get; set; } = [];
    public string SnapshotDirectory { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
