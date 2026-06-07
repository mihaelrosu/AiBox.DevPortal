namespace AiBox.DevPortal.Models;

public sealed class ExecutionPermissionProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ExecutionPermissionLevel Level { get; set; } = ExecutionPermissionLevel.None;
    public bool Enabled { get; set; } = true;
    public bool AllowReadFiles { get; set; }
    public bool AllowWriteFiles { get; set; }
    public bool AllowCreateFiles { get; set; }
    public bool AllowDeleteFiles { get; set; }
    public bool AllowRunShell { get; set; }
    public bool AllowRunDotNet { get; set; }
    public bool AllowRunDocker { get; set; }
    public bool AllowRunGit { get; set; }
    public bool AllowRunPython { get; set; }
    public bool AllowNetworkAccess { get; set; }
    public List<string> AllowedWorkingDirectories { get; set; } = [];
    public List<string> BlockedWorkingDirectories { get; set; } = [];
    public List<string> AllowedCommands { get; set; } = [];
    public List<string> BlockedCommands { get; set; } = [];
    public bool RequiresConfirmation { get; set; }
}
