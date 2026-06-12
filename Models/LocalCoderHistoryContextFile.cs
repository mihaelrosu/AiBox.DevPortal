namespace AiBox.DevPortal.Models;

public sealed class LocalCoderHistoryContextFile
{
    public string RelativePath { get; set; } = string.Empty;

    public int CharacterCount { get; set; }

    public int EstimatedTokens { get; set; }

    public string ContentHash { get; set; } = string.Empty;
}
