namespace AiBox.DevPortal.Models;

public enum LocalCoderPresetFileStatus
{
    Add,
    AlreadySelected,
    Skipped
}

public sealed class LocalCoderPresetPreview
{
    public IReadOnlyList<LocalCoderPresetPreviewFile> Files { get; set; } = [];
}

public sealed class LocalCoderPresetPreviewFile
{
    public FileSearchItem File { get; set; } = new();
    public LocalCoderPresetFileStatus Status { get; set; }
    public string SkipReason { get; set; } = string.Empty;
}
