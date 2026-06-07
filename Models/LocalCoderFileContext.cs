namespace AiBox.DevPortal.Models;

public sealed class LocalCoderFileContext
{
    public string RelativePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int CharacterCount { get; set; }
}
