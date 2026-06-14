using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Models.Agents;

public sealed class AgentRunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string ActionKey { get; set; } = string.Empty;
    public AgentMode ProfileMode { get; set; } = AgentMode.Planner;
    public string Model { get; set; } = string.Empty;
    public string UserRequest { get; set; } = string.Empty;
    public string PromptSent { get; set; } = string.Empty;
    public string ResultText { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public PatchVerificationResult? PatchVerificationResult { get; set; }
}
