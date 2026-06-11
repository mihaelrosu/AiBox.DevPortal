namespace AiBox.DevPortal.Models;

public sealed class PatchEditOperation
{
    public string FilePath { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Anchor { get; set; } = string.Empty;
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}
