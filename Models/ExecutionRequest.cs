namespace AiBox.DevPortal.Models;

public sealed class ExecutionRequest
{
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public ExecutionCommandType CommandType { get; set; } = ExecutionCommandType.Shell;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool RequiresConfirmation { get; set; }
    public bool Confirmed { get; set; }
}
