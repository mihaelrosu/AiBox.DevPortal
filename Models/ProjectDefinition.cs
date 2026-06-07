namespace AiBox.DevPortal.Models;

public sealed class ProjectDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ProjectType Type { get; set; } = ProjectType.Other;
    public string Description { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string GitRepository { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = string.Empty;
    public string BuildCommand { get; set; } = string.Empty;
    public string RunCommand { get; set; } = string.Empty;
    public string TestCommand { get; set; } = string.Empty;
    public string DefaultExecutionPermissionProfileId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
