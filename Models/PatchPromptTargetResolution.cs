namespace AiBox.DevPortal.Models;

public sealed class PatchPromptTargetResolution
{
    public bool TargetFound { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public int LineNumber { get; set; }

    public string TargetText { get; set; } = string.Empty;

    public string SurroundingContext { get; set; } = string.Empty;

    public int MatchCount { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;
}
