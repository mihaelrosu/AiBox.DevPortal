namespace AiBox.DevPortal.Models;

public sealed record PatchSuggestedTargetMatch(
    int LineNumber,
    string Snippet,
    double SimilarityScore);

public sealed record PatchSuggestedTargetDiagnostic(
    string FilePath,
    string FailedOperation,
    string TargetLabel,
    string RequestedText,
    string SuggestedTargetText,
    IReadOnlyList<PatchSuggestedTargetMatch> ClosestMatches);

public sealed record PatchSuggestedTargetSelection(
    string FilePath,
    string FailedOperation,
    string TargetLabel,
    string RequestedText,
    string SuggestedTargetText,
    int LineNumber,
    double SimilarityScore);
