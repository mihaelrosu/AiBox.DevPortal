using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModelComparisonServiceTests
{
    [Fact]
    public async Task RunComparisonAsync_SavesAllBenchmarkRunIds()
    {
        await using var context = CreateContext(_ => Task.FromResult("ok output"));

        var result = await context.Service.RunComparisonAsync(
            AgentMode.Planner,
            ["model-a", "model-b"],
            "Compare prompt");

        Assert.Equal(2, result.BenchmarkRunIds.Count);
        Assert.Equal(2, result.ComparedModels.Count);

        var latestBenchmarks = await context.BenchmarkService.GetLatestAsync(10);
        Assert.Equal(2, latestBenchmarks.Count);
        Assert.All(result.BenchmarkRunIds, benchmarkRunId => Assert.Contains(latestBenchmarks, item => item.Id == benchmarkRunId));

        var latestComparisons = await context.Service.GetLatestAsync();
        Assert.Single(latestComparisons);
        Assert.Equal(result.Id, latestComparisons[0].Id);
        Assert.Equal(result.BenchmarkRunIds, latestComparisons[0].BenchmarkRunIds);
    }

    [Fact]
    public async Task RunComparisonAsync_SelectsBestModelBySuccess()
    {
        await using var context = CreateContext(model =>
            model.Equals("model-a", StringComparison.OrdinalIgnoreCase)
                ? Task.FromException<string>(new InvalidOperationException("boom"))
                : Task.FromResult("success output"));

        var result = await context.Service.RunComparisonAsync(
            AgentMode.Verifier,
            ["model-a", "model-b"],
            "Compare prompt");

        Assert.Equal("model-b", result.BestModel);
        Assert.Contains("successful", result.BestModelReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunComparisonAsync_TieBreaksByDuration()
    {
        await using var context = CreateContext(async model =>
        {
            if (model.Equals("slow-model", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(100);
                return "same";
            }

            await Task.Delay(10);
            return "same";
        });

        var result = await context.Service.RunComparisonAsync(
            AgentMode.Reviewer,
            ["slow-model", "fast-model"],
            "Compare prompt");

        Assert.Equal("fast-model", result.BestModel);
        Assert.Contains("shortest duration", result.BestModelReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunComparisonAsync_EmptyModelListFailsClearly()
    {
        await using var context = CreateContext(_ => Task.FromResult("ok output"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Service.RunComparisonAsync(AgentMode.ToolRunner, [], "Compare prompt"));

        Assert.Contains("at least one comparison model", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TestContextBundle CreateContext(Func<string, Task<string>> generateAsync)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-model-comparison-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var environment = new TestWebHostEnvironment(root);
        var ollamaService = Substitute.For<IOllamaService>();
        ollamaService.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => generateAsync(callInfo.ArgAt<string>(0)));

        var benchmarkService = new AgentModelBenchmarkService(ollamaService, environment);
        var service = new AgentModelComparisonService(benchmarkService, environment);
        return new TestContextBundle(root, benchmarkService, service);
    }

    private sealed record TestContextBundle(string Root, AgentModelBenchmarkService BenchmarkService, AgentModelComparisonService Service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AiBox.DevPortal.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string EnvironmentName { get; set; } = "Development";
    }
}
