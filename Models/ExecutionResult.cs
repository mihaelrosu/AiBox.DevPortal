namespace AiBox.DevPortal.Models;

public sealed class ExecutionResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Rejected;
    public int? ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public ExecutionCommandType CommandType { get; set; } = ExecutionCommandType.Shell;
    public string CommandText { get; set; } = string.Empty;
}
