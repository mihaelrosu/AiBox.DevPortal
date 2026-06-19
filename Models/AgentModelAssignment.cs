using AiBox.DevPortal.Models.Agents;
using System.Text.Json.Serialization;

namespace AiBox.DevPortal.Models;

public sealed class AgentModelAssignment
{
    public AgentMode Role { get; set; } = AgentMode.Planner;
    public string PreferredModel { get; set; } = string.Empty;
    public bool UseRecommendedModel { get; set; }
    public string FallbackModel { get; set; } = string.Empty;
    public bool AllowFallback { get; set; }

    [JsonIgnore]
    public string SelectedModel { get; set; } = string.Empty;

    [JsonIgnore]
    public string RoutingReason { get; set; } = string.Empty;

    [JsonIgnore]
    public bool FallbackUsed { get; set; }
}
