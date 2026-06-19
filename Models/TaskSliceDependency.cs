namespace AiBox.DevPortal.Models;

public sealed class TaskSliceDependency
{
    public string SliceId { get; set; } = string.Empty;
    public string DependsOnSliceId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
