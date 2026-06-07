namespace AiBox.DevPortal.Models;

public enum ExecutionStatus
{
    Rejected,
    WaitingForConfirmation,
    Running,
    Completed,
    Failed,
    TimedOut
}
