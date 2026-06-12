using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class LocalCoderContextServiceTests
{
    [Fact]
    public async Task LoadAsync_MultipleTextFiles_LoadsAllContent()
    {
        var root = CreateRoot();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "One.cs"), "one");
            await File.WriteAllTextAsync(Path.Combine(root, "Two.razor"), "two");

            var contexts = await new LocalCoderContextService().LoadAsync(root, ["One.cs", "Two.razor"]);

            Assert.Equal(2, contexts.Count);
            Assert.Equal(["one", "two"], contexts.Select(context => context.Content));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_PathOutsideProject_RejectsFile()
    {
        var root = CreateRoot();

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new LocalCoderContextService().LoadAsync(root, ["../outside.cs"]));

            Assert.Contains("outside the selected project root", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_BinaryFile_RejectsFile()
    {
        var root = CreateRoot();

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "Binary.cs"), [1, 0, 2]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new LocalCoderContextService().LoadAsync(root, ["Binary.cs"]));

            Assert.Contains("appears to be binary", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_TotalSizeOverLimit_RejectsContext()
    {
        var root = CreateRoot();

        try
        {
            var content = new string('a', 180 * 1024);
            await File.WriteAllTextAsync(Path.Combine(root, "One.cs"), content);
            await File.WriteAllTextAsync(Path.Combine(root, "Two.cs"), content);
            await File.WriteAllTextAsync(Path.Combine(root, "Three.cs"), content);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new LocalCoderContextService().LoadAsync(root, ["One.cs", "Two.cs", "Three.cs"]));

            Assert.Contains("500 KB total limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_SingleFileOverLimit_RejectsFile()
    {
        var root = CreateRoot();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Large.cs"), new string('a', 201 * 1024));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new LocalCoderContextService().LoadAsync(root, ["Large.cs"]));

            Assert.Contains("200 KB context limit", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("bin/Generated.cs")]
    [InlineData("obj/Generated.cs")]
    [InlineData(".git/Generated.cs")]
    [InlineData("node_modules/Generated.js")]
    [InlineData("wwwroot/lib/Generated.js")]
    public async Task LoadAsync_GeneratedFolder_MarksFileForConfirmation(string relativePath)
    {
        var root = CreateRoot();

        try
        {
            var fullPath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, "generated");

            var context = Assert.Single(await new LocalCoderContextService().LoadAsync(root, [relativePath]));

            Assert.True(context.IsGeneratedFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(LocalCoderContextPreset.CurrentPage, "Components/Pages/Coder.razor")]
    [InlineData(LocalCoderContextPreset.PageAndComponents, "Components/Pages/Coder.razor,Components/Coder/Child.razor")]
    [InlineData(LocalCoderContextPreset.PageAndServices, "Components/Pages/Coder.razor,Services/CoderConsoleService.cs")]
    [InlineData(LocalCoderContextPreset.FullFeatureContext, "Components/Pages/Coder.razor,Components/Coder/Child.razor,Models/CoderState.cs,Services/CoderConsoleService.cs")]
    public async Task PreviewPresetAsync_ReturnsExpectedFiles(
        LocalCoderContextPreset preset,
        string expectedPathsText)
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "Components/Pages/Coder.razor");
            await WriteFileAsync(root, "Components/Coder/Child.razor");
            await WriteFileAsync(root, "Services/CoderConsoleService.cs");
            await WriteFileAsync(root, "Services/UnrelatedService.cs");
            await WriteFileAsync(root, "Models/CoderState.cs");
            await WriteFileAsync(root, "Models/UnrelatedState.cs");
            await WriteFileAsync(root, "bin/CoderGenerated.cs");
            await WriteFileAsync(root, "obj/CoderGenerated.cs");
            await WriteFileAsync(root, ".git/CoderGenerated.cs");
            await WriteFileAsync(root, "node_modules/CoderGenerated.js");

            var preview = await new LocalCoderContextService().PreviewPresetAsync(
                root,
                "Components/Pages/Coder.razor",
                preset,
                []);

            Assert.Equal(
                expectedPathsText.Split(',').OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
                preview.Files
                    .Where(file => file.Status == LocalCoderPresetFileStatus.Add)
                    .Select(file => file.File.RelativePath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewPresetAsync_ReportsSkipReasons()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "Components/Pages/Coder.razor");
            await WriteFileAsync(root, "Components/Coder/Child.razor");
            await WriteFileAsync(root, "Components/Coder/Large.razor", new string('a', 201 * 1024));
            await WriteFileAsync(root, "Components/Coder/TotalLimit.razor", new string('a', 100 * 1024));
            await WriteFileAsync(root, "Components/Coder/Binary.razor", "\0binary");
            await WriteFileAsync(root, "Components/Coder/bin/Ignored.razor");
            await WriteFileAsync(root, "Models/CoderState.cs");
            await WriteFileAsync(root, "Services/CoderService.cs");
            await WriteFileAsync(root, "Selected.cs", new string('a', 450 * 1024));

            var preview = await new LocalCoderContextService().PreviewPresetAsync(
                root,
                "Components/Pages/Coder.razor",
                LocalCoderContextPreset.FullFeatureContext,
                ["Components/Coder/Child.razor", "Selected.cs"]);

            Assert.Contains(preview.Files, item =>
                item.File.RelativePath == "Components/Coder/Child.razor"
                && item.Status == LocalCoderPresetFileStatus.AlreadySelected
                && item.SkipReason == "already selected");
            Assert.Contains(preview.Files, item => item.SkipReason == "binary file");
            Assert.Contains(preview.Files, item => item.SkipReason == "too large");
            Assert.Contains(preview.Files, item => item.SkipReason == "ignored folder");
            Assert.Contains(preview.Files, item => item.SkipReason == "total context limit");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_ReportsRestoredModifiedAndSkippedFiles()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "Same.cs", "same");
            await WriteFileAsync(root, "Modified.cs", "new");
            await WriteFileAsync(root, "bin/Ignored.cs", "ignored");
            await WriteFileAsync(root, "Binary.cs", "\0binary");
            await WriteFileAsync(root, "Large.cs", new string('a', 201 * 1024));

            var result = await new LocalCoderContextService().RestoreAsync(
                root,
                [
                    Snapshot("Same.cs", "same"),
                    Snapshot("Modified.cs", "old"),
                    Snapshot("bin/Ignored.cs", "ignored"),
                    Snapshot("Binary.cs", "binary"),
                    Snapshot("Large.cs", "large"),
                    Snapshot("../Outside.cs", "outside")
                ]);

            Assert.Equal(2, result.RestoredContexts.Count);
            Assert.Contains(result.Files, file => file.RelativePath == "Same.cs" && file.Restored && !file.ModifiedSinceRun);
            Assert.Contains(result.Files, file => file.RelativePath == "Modified.cs" && file.Restored && file.ModifiedSinceRun);
            Assert.Contains(result.Files, file => file.SkipReason == "ignored folder");
            Assert.Contains(result.Files, file => file.SkipReason == "binary file");
            Assert.Contains(result.Files, file => file.SkipReason == "too large");
            Assert.Contains(result.Files, file => file.SkipReason == "outside project root");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"local-coder-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteFileAsync(string root, string relativePath, string? content = null)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content ?? relativePath);
    }

    private static LocalCoderHistoryContextFile Snapshot(string relativePath, string content) =>
        new()
        {
            RelativePath = relativePath,
            CharacterCount = content.Length,
            EstimatedTokens = Math.Max(1, content.Length / 4),
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()
        };
}
