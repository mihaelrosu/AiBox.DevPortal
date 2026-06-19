namespace AiBox.DevPortal.Models.Agents;

public sealed class AgentModeProfile
{
    public string Id { get; set; } = string.Empty;
    public AgentMode Mode { get; set; } = AgentMode.Planner;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PreferredModel { get; set; } = string.Empty;
    public string FallbackModel { get; set; } = string.Empty;
    public bool AllowFallback { get; set; }
    public string RulesSummary { get; set; } = string.Empty;
}
