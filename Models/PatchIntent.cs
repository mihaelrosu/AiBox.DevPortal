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
    public string AllowedScope { get; set; } = string.Empty;
    public PatchIntentChangeType ExpectedChangeType { get; set; } = PatchIntentChangeType.Unknown;
    public IReadOnlyList<string> MustNotChange { get; set; } = [];
    public string VerificationCommand { get; set; } = "dotnet build";
}

public sealed class PatchIntentValidation
{
    public PatchIntentMatchStatus Status { get; set; } = PatchIntentMatchStatus.PartiallyMatches;
    public string DetectedChangeType { get; set; } = string.Empty;
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public bool IsBlocking => Status == PatchIntentMatchStatus.DoesNotMatch;
}
