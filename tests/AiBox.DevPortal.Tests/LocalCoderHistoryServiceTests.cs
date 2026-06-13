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

    [Fact]
    public async Task AddEntryAsync_RepairSummary_PersistsAndLoads()
    {
        var root = CreateRoot();

        try
        {
            var service = CreateService(root);
            await service.AddEntryAsync(new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                Success = true,
                RepairSummary = new PatchPreviewRepairSummary
                {
                    OriginalOperation = "replace",
                    RepairAttempt = "insert_after",
                    RepairResult = "Success",
                    ValidationError = string.Empty
                }
            });

            var entry = Assert.Single(await service.GetHistoryAsync());
            Assert.NotNull(entry.RepairSummary);
            Assert.Equal("replace", entry.RepairSummary!.OriginalOperation);
            Assert.Equal("insert_after", entry.RepairSummary.RepairAttempt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddEntryAsync_PromptAudit_PersistsAndLoads()
    {
        var root = CreateRoot();

        try
        {
            var service = CreateService(root);
            await service.AddEntryAsync(new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                Success = true,
                PromptAudit = new PatchPromptAudit
                {
                    ContextFiles = ["Example.cs"],
                    ContextCharactersLoaded = 42,
                    ContextTextCharactersSent = 120,
                    ContextTextFirst500Chars = "FIRST",
                    ContextTextLast500Chars = "LAST"
                }
            });

            var entry = Assert.Single(await service.GetHistoryAsync());
            Assert.NotNull(entry.PromptAudit);
            Assert.Equal(42, entry.PromptAudit!.ContextCharactersLoaded);
            Assert.Equal("FIRST", entry.PromptAudit.ContextTextFirst500Chars);
            Assert.Equal("LAST", entry.PromptAudit.ContextTextLast500Chars);
            Assert.Contains("Example.cs", entry.PromptAudit.ContextFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddEntryAsync_PromptTargetResolution_PersistsAndLoads()
    {
        var root = CreateRoot();

        try
        {
            var service = CreateService(root);
            await service.AddEntryAsync(new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                Success = true,
                PromptTargetResolution = new PatchPromptTargetResolution
                {
                    TargetFound = true,
                    Operation = "insert_before",
                    FilePath = "Example.cs",
                    LineNumber = 9,
                    TargetText = "public sealed class Example {}",
                    SurroundingContext = "8: namespace Demo\n9: public sealed class Example {}",
                    MatchCount = 1
                }
            });

            var entry = Assert.Single(await service.GetHistoryAsync());
            Assert.NotNull(entry.PromptTargetResolution);
            Assert.True(entry.PromptTargetResolution!.TargetFound);
            Assert.Equal("insert_before", entry.PromptTargetResolution.Operation);
            Assert.Equal("Example.cs", entry.PromptTargetResolution.FilePath);
            Assert.Equal(9, entry.PromptTargetResolution.LineNumber);
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
