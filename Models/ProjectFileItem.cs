namespace AiBox.DevPortal.Models;

public sealed class ProjectFileItem
{
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
