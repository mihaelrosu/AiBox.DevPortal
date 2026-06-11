using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchEditOperationServiceTests
{
    [Fact]
    public async Task BuildAsync_HtmlAnchorFallsBackToRadzenText_ProducesPatchAndLogsStrategy()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-anchor-test-{Guid.NewGuid():N}");
        var relativePath = "Components/Pages/Coder.razor";
        var filePath = Path.Combine(projectRoot, relativePath);
        const string content = "<RadzenText Text=\"Patch Queue\" TextStyle=\"TextStyle.H5\" />";
        var logger = new ListLogger<PatchEditOperationService>();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, content);

            var service = new PatchEditOperationService(logger);
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                """
                {
                  "operations": [
                    {
                      "filePath": "Components/Pages/Coder.razor",
                      "operation": "insert_after",
                      "anchor": "<h3>Patch Queue</h3>",
                      "newText": "\n<RadzenAlert />"
                    }
                  ]
                }
                """);

            var change = Assert.Single(result.FileChanges);
            Assert.Equal($"{content}{Environment.NewLine}<RadzenAlert />", change.NewContent);
            Assert.Contains(logger.Entries, entry => entry.Contains("Original anchor: <h3>Patch Queue</h3>", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Normalized anchor: Patch Queue", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Match strategy: ExactText", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_JsonMarkdownFence_ParsesOperations()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-fenced-json-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        var filePath = Path.Combine(projectRoot, relativePath);
        const string content = "public class Example {}";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                """
                ```json
                {
                  "operations": [
                    {
                      "filePath": "Example.cs",
                      "operation": "insert_after",
                      "anchor": "public class Example {}",
                      "newText": "\n// Added"
                    }
                  ]
                }
                ```
                """);

            var change = Assert.Single(result.FileChanges);
            Assert.Equal($"{content}{Environment.NewLine}// Added", change.NewContent);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_UnclosedMarkdownFence_FailsJsonValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-malformed-fence-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);
            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = "Example.cs", Content = "content" }],
                    """
                    ```json
                    {
                      "operations": []
                    }
                    """));

            Assert.Contains("JSON parse error", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
