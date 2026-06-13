namespace AiBox.DevPortal.Models;

public sealed class PatchPromptAudit
{
    public IReadOnlyList<string> ContextFiles { get; set; } = [];

    public int ContextCharactersLoaded { get; set; }

    public int ContextTextCharactersSent { get; set; }

    public string ContextTextFirst500Chars { get; set; } = string.Empty;

    public string ContextTextLast500Chars { get; set; } = string.Empty;
}
