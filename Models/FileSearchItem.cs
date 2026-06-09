namespace AiBox.DevPortal.Models;

public sealed class FileSearchItem
{
    public string FileName { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string Extension { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string DisplayText => RelativePath;
}
