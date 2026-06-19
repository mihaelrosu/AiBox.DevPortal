using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Models;

public sealed class AgentModelBenchmarkRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AgentMode AgentRole { get; set; } = AgentMode.Planner;
    public string ModelName { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long DurationMs { get; set; }
    public int OutputLength { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
