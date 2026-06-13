namespace AiBox.DevPortal.Models;

public enum PatchIntentChangeType
{
    Unknown,
    Update,
    Add,
    Remove,
    Move,
    Refactor
}

public enum PatchPrimaryIntent
{
    Unknown,
    ReadOnly,
    Modify
}

public enum PatchIntentMatchStatus
{
    MatchesIntent,
    PartiallyMatches,
    DoesNotMatch
}

public sealed class PatchIntent
{
    public string Goal { get; set; } = string.Empty;
    public IReadOnlyList<string> AllowedPaths { get; set; } = [];
    public IReadOnlyList<string> ProtectedPaths { get; set; } = [];
    public string AllowedScope { get; set; } = string.Empty;
    public PatchPrimaryIntent PrimaryIntent { get; set; } = PatchPrimaryIntent.Unknown;
    public PatchIntentChangeType ExpectedChangeType { get; set; } = PatchIntentChangeType.Unknown;
    public IReadOnlyList<string> MustNotChange { get; set; } = [];
    public string VerificationCommand { get; set; } = "dotnet build";
}

public sealed class PatchIntentValidation
{
    public PatchIntentMatchStatus Status { get; set; } = PatchIntentMatchStatus.PartiallyMatches;
    public string DetectedChangeType { get; set; } = string.Empty;
    public string ScopeMode { get; set; } = string.Empty;
    public IReadOnlyList<string> RequestedFiles { get; set; } = [];
    public IReadOnlyList<string> ModifiedFiles { get; set; } = [];
    public IReadOnlyList<string> ContextFiles { get; set; } = [];
    public IReadOnlyList<string> ProtectedFiles { get; set; } = [];
    public IReadOnlyList<PatchIntentFileEvaluation> FileEvaluations { get; set; } = [];
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public bool IsBlocking => Status == PatchIntentMatchStatus.DoesNotMatch;
}

public sealed class PatchIntentFileEvaluation
{
    public string RelativePath { get; set; } = string.Empty;
    public bool InContext { get; set; }
    public bool InScope { get; set; }
    public bool MatchesRequestedFile { get; set; }
    public bool ExplicitlyAllowed { get; set; }
    public bool ExplicitlyProtected { get; set; }
    public bool Protected { get; set; }
}
