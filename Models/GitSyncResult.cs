namespace AiBox.DevPortal.Models;

public sealed class GitSyncResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool CommitAttempted { get; set; }
    public bool CommitSucceeded { get; set; }
    public string CommitHash { get; set; } = string.Empty;
    public bool PushAttempted { get; set; }
    public bool PushSucceeded { get; set; }
    public string GitMessage { get; set; } = string.Empty;
}
