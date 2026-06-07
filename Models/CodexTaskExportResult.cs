namespace AiBox.DevPortal.Models;

public sealed class CodexTaskExportResult
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string SourceRunId { get; set; } = string.Empty;
}
