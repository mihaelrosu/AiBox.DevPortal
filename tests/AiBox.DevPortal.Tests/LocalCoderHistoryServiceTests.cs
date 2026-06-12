using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class LocalCoderHistoryServiceTests
{
    [Fact]
    public async Task GetHistoryAsync_LegacyJson_DefaultsContextSnapshot()
    {
        var root = CreateRoot();

        try
        {
            var dataDirectory = Path.Combine(root, "Data");
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "coder-task-history.json"),
                """[{"id":"legacy","createdAt":"2026-06-12T00:00:00Z","actionType":"createPlan","projectPath":"/project","model":"model","task":"task","selectedFiles":[],"loadedContextFiles":[],"success":true,"message":"legacy"}]""");

            var entry = Assert.Single(await CreateService(root).GetHistoryAsync());

            Assert.Equal(0, entry.ContextFileCount);
            Assert.Equal(0, entry.ContextTotalCharacters);
            Assert.Equal(0, entry.ContextEstimatedTokens);
            Assert.Empty(entry.ContextFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddEntryAsync_ContextSnapshot_PersistsMetadataWithoutContent()
    {
        var root = CreateRoot();

        try
        {
            var service = CreateService(root);
            await service.AddEntryAsync(new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.CreatePlan,
                ContextFileCount = 1,
                ContextTotalCharacters = 12,
                ContextEstimatedTokens = 3,
                ContextFiles =
                [
                    new LocalCoderHistoryContextFile
                    {
                        RelativePath = "Example.cs",
                        CharacterCount = 12,
                        EstimatedTokens = 3,
                        ContentHash = "abc123"
                    }
                ]
            });

            var entry = Assert.Single(await service.GetHistoryAsync());
            var file = Assert.Single(entry.ContextFiles);
            Assert.Equal("abc123", file.ContentHash);

            var json = await File.ReadAllTextAsync(Path.Combine(root, "Data", "coder-task-history.json"));
            Assert.DoesNotContain("\"content\":", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalCoderHistoryService CreateService(string root)
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(root);
        return new LocalCoderHistoryService(environment);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-coder-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
