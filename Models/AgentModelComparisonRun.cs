using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Models;

public sealed class AgentModelComparisonRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AgentMode AgentRole { get; set; } = AgentMode.Planner;
    public string Prompt { get; set; } = string.Empty;
    public IReadOnlyList<string> ComparedModels { get; set; } = [];
    public IReadOnlyList<string> BenchmarkRunIds { get; set; } = [];
    public string BestModel { get; set; } = string.Empty;
    public string BestModelReason { get; set; } = string.Empty;
}
