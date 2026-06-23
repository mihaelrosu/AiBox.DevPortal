namespace AiBox.DevPortal.Models;

public sealed class ProjectKnowledgeIndex
{
    public string ProjectPath { get; set; } = string.Empty;
    public DateTime RebuiltAtUtc { get; set; }
    public IReadOnlyList<ProjectKnowledgeItem> Items { get; set; } = [];
}
