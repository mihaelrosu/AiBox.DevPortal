namespace AiBox.DevPortal.Models;

public sealed class ProjectHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = [];
}
