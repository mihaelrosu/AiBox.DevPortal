using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentInstructionServiceTests
{
    [Fact]
    public async Task FindRelevantAgentFilesAsync_ReturnsRootToLeafHierarchy()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "AGENTS.md", "root instructions");
            await WriteFileAsync(root, "Components/AGENTS.md", "components instructions");
            await WriteFileAsync(root, "Components/Coder/AGENTS.md", "coder instructions");
            await WriteFileAsync(root, "Components/Other/AGENTS.md", "other instructions");

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(root);

            var service = new AgentInstructionService(environment);
            var files = await service.FindRelevantAgentFilesAsync(["Components/Coder/CoderKnowledgePanel.razor"]);

            Assert.Equal(["AGENTS.md", "Components/AGENTS.md", "Components/Coder/AGENTS.md"], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildContextAsync_LoadsRelevantAgentFilesInOrder()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "AGENTS.md", "root instructions");
            await WriteFileAsync(root, "Components/AGENTS.md", "components instructions");
            await WriteFileAsync(root, "Components/Coder/AGENTS.md", "coder instructions");

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(root);

            var service = new AgentInstructionService(environment);
            var context = await service.BuildContextAsync(["Components/Coder/CoderKnowledgePanel.razor"]);

            Assert.Equal(["AGENTS.md", "Components/AGENTS.md", "Components/Coder/AGENTS.md"], context.RelevantAgentFiles);
            Assert.Equal(3, context.Files.Count);
            Assert.Equal("root instructions", context.Files[0].Content);
            Assert.Equal("components instructions", context.Files[1].Content);
            Assert.Equal("coder instructions", context.Files[2].Content);
            Assert.Contains("FILE: AGENTS.md", context.CombinedText, StringComparison.Ordinal);
            Assert.Contains("FILE: Components/Coder/AGENTS.md", context.CombinedText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-instruction-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteFileAsync(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }
}
