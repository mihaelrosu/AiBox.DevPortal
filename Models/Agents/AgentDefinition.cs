namespace AiBox.DevPortal.Models.Agents;

public sealed class AgentDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentRole Role { get; set; } = AgentRole.Custom;
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public string SystemPrompt { get; set; } = string.Empty;
    public string ExecutionPermissionProfileId { get; set; } = string.Empty;
    public List<AgentPermission> Permissions { get; set; } = [];
}
