using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Models;

public sealed class AgentModelRecommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AgentMode AgentRole { get; set; } = AgentMode.Planner;
    public string RecommendedModel { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int SourceRunCount { get; set; }
    public bool HasRecommendation { get; set; }
}
