namespace AiBox.DevPortal.Models;

public sealed class TaskPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalRequest { get; set; } = string.Empty;
    public List<TaskPlanSlice> Slices { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
