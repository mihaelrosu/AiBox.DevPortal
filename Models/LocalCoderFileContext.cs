namespace AiBox.DevPortal.Models;

public sealed class LocalCoderFileContext
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsTruncated { get; set; }
    public int CharacterCount => Content?.Length ?? 0;
    public int EstimatedTokens => Math.Max(1, CharacterCount / 4);
    public bool IsLargeFile => CharacterCount >= 100 * 1024;
    public bool IsEmptyFile => CharacterCount == 0;
    public bool IsGeneratedFile { get; set; }
}
