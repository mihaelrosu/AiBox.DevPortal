namespace AiBox.DevPortal.Models;

public enum ExecutionPermissionLevel
{
    None,
    ReadOnly,
    ProjectWrite,
    BuildAndTest,
    DockerMaintenance,
    FullLocalAdmin
}
