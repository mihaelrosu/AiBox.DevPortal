namespace AiBox.DevPortal.Models;

public sealed class AgentInstructionContext
{
    public IReadOnlyList<string> RelevantAgentFiles { get; set; } = [];
    public IReadOnlyList<AgentInstructionFile> Files { get; set; } = [];
    public string CombinedText { get; set; } = string.Empty;
    public bool HasFiles => Files.Count > 0;
}

public sealed class AgentInstructionFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int CharacterCount => Content.Length;
}
