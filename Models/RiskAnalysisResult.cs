namespace AiBox.DevPortal.Models;

public sealed class RiskAnalysisResult
{
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;
    public int TotalScore { get; set; }
    public IReadOnlyList<RiskFactor> Factors { get; set; } = [];
    public bool RequiresManualApproval { get; set; }
    public string Summary { get; set; } = string.Empty;
}
