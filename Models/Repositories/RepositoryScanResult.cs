namespace AiBox.DevPortal.Models.Repositories;

public sealed class RepositoryScanResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public IReadOnlyList<string> Directories { get; set; } = [];
    public IReadOnlyList<RepositoryFileSummary> Files { get; set; } = [];
    public int SkippedDirectoryCount { get; set; }
    public int SkippedFileCount { get; set; }
    public bool Truncated { get; set; }
}
