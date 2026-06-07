namespace AiBox.DevPortal.Models.Agents;

public enum LocalCoderTaskStatus
{
    Draft,
    Planned,
    PatchGenerated,
    BuildSucceeded,
    BuildFailed,
    Reviewed,
    ApplyDisabled,
    Applied,
    Failed
}
