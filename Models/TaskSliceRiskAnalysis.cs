namespace AiBox.DevPortal.Models;

public sealed class TaskSliceRiskAnalysis
{
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string SliceTitle { get; set; } = string.Empty;
    public TaskSliceRiskLevel RiskLevel { get; set; } = TaskSliceRiskLevel.Low;
    public int RiskScore { get; set; }
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public DateTime AnalyzedAtUtc { get; set; } = DateTime.UtcNow;
}
