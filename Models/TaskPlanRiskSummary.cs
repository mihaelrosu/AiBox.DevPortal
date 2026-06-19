namespace AiBox.DevPortal.Models;

public sealed class TaskPlanRiskSummary
{
    public string PlanId { get; set; } = string.Empty;
    public int TotalSlices { get; set; }
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;
    public int TotalScore { get; set; }
    public IReadOnlyList<RiskFactor> Factors { get; set; } = [];
    public bool RequiresManualApproval { get; set; }
    public RiskLevel HighestRiskLevel { get; set; } = RiskLevel.Low;
    public int HighestScore { get; set; }
    public int ManualApprovalRequiredCount { get; set; }
    public string Summary { get; set; } = string.Empty;
}
