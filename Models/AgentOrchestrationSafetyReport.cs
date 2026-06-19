namespace AiBox.DevPortal.Models;

public sealed class AgentOrchestrationSafetyReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public RiskLevel HighestRiskLevel { get; set; } = RiskLevel.Low;
    public int TotalChangedFiles { get; set; }
    public bool RequiresManualApproval { get; set; }
    public bool BlocksAutoApply { get; set; }
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = string.Empty;
}
