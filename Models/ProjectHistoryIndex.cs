namespace AiBox.DevPortal.Models;

public sealed class ProjectHistoryIndex
{
    public string ProjectPath { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public List<ProjectHistoryItem> Items { get; set; } = [];
}
