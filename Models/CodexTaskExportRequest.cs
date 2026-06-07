namespace AiBox.DevPortal.Models;

public sealed class CodexTaskExportRequest
{
    public bool IncludeGoal { get; set; } = true;
    public bool IncludeProject { get; set; } = true;
    public bool IncludePlannerResult { get; set; } = true;
    public bool IncludeArchitectResult { get; set; } = true;
    public bool IncludeCodingResult { get; set; } = true;
    public bool IncludeVerifierResult { get; set; } = true;
    public string CustomInstructions { get; set; } = string.Empty;
    public CodexTaskOutputFormat OutputFormat { get; set; } = CodexTaskOutputFormat.Markdown;
}
