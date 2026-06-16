using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceExecutionServiceTests
{
    [Fact]
    public async Task RecordSliceExecutionResultAsync_WritesGeneratePatchFailureHistory()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"task-slice-execution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);

        try
        {
            var service = new TaskSliceExecutionService(new TestWebHostEnvironment(projectRoot));
            await service.RecordSliceExecutionResultAsync(new TaskSliceExecutionResult
            {
                PlanId = "plan-1",
                SliceId = "slice-1",
                SliceTitle = "Slice 1",
                RequestedAction = "GeneratePatch",
                Success = false,
                BuildSuccess = false,
                VerificationSuccess = false,
                Summary = "This slice has no editable target files or valid create targets. Select files, add TargetFiles, or regenerate the plan.",
                Errors = ["This slice has no editable target files or valid create targets. Select files, add TargetFiles, or regenerate the plan."],
                ExecutedAt = DateTime.UtcNow
            });

            var historyPath = Path.Combine(projectRoot, "Data", "task-slice-execution-history.json");
            var history = await JsonSerializer.DeserializeAsync<List<TaskSliceExecutionResult>>(
                File.OpenRead(historyPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var entry = Assert.Single(history ?? []);
            Assert.Equal("GeneratePatch", entry.RequestedAction);
            Assert.False(entry.Success);
            Assert.Contains("no editable target files", entry.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }
}
