namespace AiBox.DevPortal.Models;

public sealed class LocalCoderContextRestoreResult
{
    public IReadOnlyList<LocalCoderContextRestoreFile> Files { get; set; } = [];

    public IReadOnlyList<LocalCoderFileContext> RestoredContexts { get; set; } = [];
}

public sealed class LocalCoderContextRestoreFile
{
    public string RelativePath { get; set; } = string.Empty;

    public bool Restored { get; set; }

    public bool ModifiedSinceRun { get; set; }

    public string SkipReason { get; set; } = string.Empty;
}
