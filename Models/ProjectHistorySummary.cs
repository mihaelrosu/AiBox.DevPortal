namespace AiBox.DevPortal.Models;

public sealed class ProjectHistorySummary
{
    public string ProjectPath { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public List<string> CompletedFeatures { get; set; } = [];
    public List<string> PendingFeatures { get; set; } = [];
    public List<string> FailedSlices { get; set; } = [];
    public List<string> AppliedPatches { get; set; } = [];
    public List<string> RecommendedNextSlices { get; set; } = [];
    public List<string> KnownIssues { get; set; } = [];
}
