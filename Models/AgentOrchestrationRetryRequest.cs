namespace AiBox.DevPortal.Models;

public sealed class AgentOrchestrationRetryRequest
{
    public string RunId { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
