namespace AiBox.DevPortal.Models;

public sealed class ToolAgentResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public IReadOnlyList<string> Evidence { get; set; } = [];
    public IReadOnlyList<string> ExecutedTools { get; set; } = [];
}
