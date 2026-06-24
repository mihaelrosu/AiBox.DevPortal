namespace AiBox.DevPortal.Models;

public sealed class ToolAgentRequest
{
    public string UserRequest { get; set; } = string.Empty;
    public IReadOnlyList<string> SelectedFiles { get; set; } = [];
    public IReadOnlyList<string> AllowedTools { get; set; } = [];
}