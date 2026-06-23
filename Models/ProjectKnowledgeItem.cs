namespace AiBox.DevPortal.Models;

public sealed class ProjectKnowledgeItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
    public IReadOnlyList<string> Classes { get; set; } = [];
    public IReadOnlyList<string> Interfaces { get; set; } = [];
    public IReadOnlyList<string> Records { get; set; } = [];
}
