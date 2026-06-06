namespace AiBox.DevPortal.Models;

public sealed class ComfyUiWorkflowFile
{
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset Modified { get; set; }
    public long SizeBytes { get; set; }
}
