namespace AiBox.DevPortal.Models;

public sealed class PatchFileChange
{
    public string RelativePath { get; set; } = string.Empty;
    public string OldContent { get; set; } = string.Empty;
    public string NewContent { get; set; } = string.Empty;
}
