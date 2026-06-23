using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ContextSuggestionService
{
    public IReadOnlyList<string> SuggestFiles(
        string userRequest,
        ProjectKnowledgeIndex knowledgeIndex)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            return [];
        }

        if (knowledgeIndex.Items.Count == 0)
        {
            return [];
        }

        var tokens = userRequest
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return knowledgeIndex.Items
            .Where(item =>
                tokens.Any(token =>
                    item.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.RelativePath)
            .Distinct()
            .Take(10)
            .ToList();
    }
}