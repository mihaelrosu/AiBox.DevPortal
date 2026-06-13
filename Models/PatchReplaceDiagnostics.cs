namespace AiBox.DevPortal.Models;

public sealed record PatchReplaceClosestMatch(
    int LineNumber,
    string Snippet,
    double SimilarityScore);

public sealed record PatchReplaceDiagnostic(
    string FilePath,
    string RequestedOldText,
    string SuggestedReplacementTarget,
    IReadOnlyList<PatchReplaceClosestMatch> ClosestMatches);
