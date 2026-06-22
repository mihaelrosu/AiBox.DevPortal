namespace AiBox.DevPortal.Models.Agents;

public sealed class AgentExecutionPolicy
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool AllowModelCalls { get; set; }
    public bool AllowToolCalls { get; set; }
    public bool AllowShellCommands { get; set; }
    public bool AllowPatchGeneration { get; set; }
    public bool AllowPatchApply { get; set; }
    public bool RequireHumanApproval { get; set; }
    public bool AutoRestoreOnVerificationFailure { get; set; }
    public int MaxFilesChanged { get; set; }
    public int MaxPatchOperations { get; set; }
    public int MaxExecutionMinutes { get; set; }
    public List<string> AllowedDirectories { get; set; } = [];
    public List<string> BlockedDirectories { get; set; } = [];
}
