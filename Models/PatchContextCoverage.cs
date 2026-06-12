namespace AiBox.DevPortal.Models;

public enum PatchContextCoverageCategory
{
    ContextFile,
    RelatedFile,
    UnknownFile
}

public sealed class PatchContextCoverage
{
    public IReadOnlyList<PatchContextCoverageFile> Files { get; set; } = [];
    public int RiskScore { get; set; }
    public bool HasFilesOutsideContext => Files.Any(file => file.Category != PatchContextCoverageCategory.ContextFile);
}

public sealed class PatchContextCoverageFile
{
    public string RelativePath { get; set; } = string.Empty;
    public PatchContextCoverageCategory Category { get; set; }
    public IReadOnlyList<string> RiskReasons { get; set; } = [];
}
