using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ContextSuggestionServiceTests
{
    [Fact]
    public void ReturnsEmptyForEmptyRequest()
    {
        var service = new ContextSuggestionService();

        var index = new ProjectKnowledgeIndex
        {
            Items =
            [
                new ProjectKnowledgeItem
                {
                    RelativePath = "Models/Test.cs",
                    Name = "Test"
                }
            ]
        };

        var result = service.SuggestFiles(string.Empty, index);

        Assert.Empty(result);
    }

    [Fact]
    public void ReturnsMatchingFileByName()
    {
        var service = new ContextSuggestionService();

        var index = new ProjectKnowledgeIndex
        {
            Items =
            [
                new ProjectKnowledgeItem
                {
                    RelativePath = "Models/AgentRunHistory.cs",
                    Name = "AgentRunHistory"
                }
            ]
        };

        var result = service.SuggestFiles("history", index);

        Assert.Single(result);
        Assert.Equal("Models/AgentRunHistory.cs", result[0]);
    }

    [Fact]
    public void ReturnsMatchingFileByPath()
    {
        var service = new ContextSuggestionService();

        var index = new ProjectKnowledgeIndex
        {
            Items =
            [
                new ProjectKnowledgeItem
                {
                    RelativePath = "Services/AgentRunTimelineService.cs",
                    Name = "AgentRunTimelineService"
                }
            ]
        };

        var result = service.SuggestFiles("timeline", index);

        Assert.Single(result);
        Assert.Equal("Services/AgentRunTimelineService.cs", result[0]);
    }
}