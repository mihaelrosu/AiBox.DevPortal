namespace AiBox.DevPortal.Models;

public enum LocalCoderPlanTaskStatus
{
    Pending,
    InProgress,
    PatchGenerated,
    Applied,
    Verified,
    Failed,
    Skipped
}

public sealed class LocalCoderPlanTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Order { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public LocalCoderPlanTaskStatus Status { get; set; } = LocalCoderPlanTaskStatus.Pending;

    public string StatusMessage { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; set; }
}
