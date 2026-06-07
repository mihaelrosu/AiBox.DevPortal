namespace AiBox.DevPortal.Models.Agents;

public sealed class LocalCoderTaskHistoryRecord
{
    public string Id { get; set; } = string.Empty;
    public LocalCoderTask Task { get; set; } = new();
    public string PlanText { get; set; } = string.Empty;
    public string DiffText { get; set; } = string.Empty;
    public string BuildOutput { get; set; } = string.Empty;
    public string ReviewText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
