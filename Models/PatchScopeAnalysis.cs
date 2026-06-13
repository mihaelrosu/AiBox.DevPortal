namespace AiBox.DevPortal.Models;

public enum PatchScopeStatus
{
    InScope,
    OutOfScope
}

public sealed class PatchScopeAnalysis
{
    public PatchScopeMode Mode { get; set; }
    public IReadOnlyList<string> AllowedFolders { get; set; } = [];
    public IReadOnlyList<string> AllowedCreateFolders { get; set; } = [];
    public IReadOnlyList<PatchScopeFileResult> Files { get; set; } = [];
    public string WarningMessage { get; set; } = string.Empty;
    public bool IsBlocking { get; set; }
    public bool HasOutOfScopeFiles => Files.Any(file => file.Status == PatchScopeStatus.OutOfScope);
}

public sealed class PatchScopeFileResult
{
    public string RelativePath { get; set; } = string.Empty;
    public PatchScopeStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsCreate { get; set; }
    public string ContextRepresentativePath { get; set; } = string.Empty;
}
