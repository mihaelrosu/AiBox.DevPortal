using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModelRecommendationServiceTests
{
    [Fact]
    public async Task RefreshAsync_RecommendsModelWithMostSuccessfulRuns()
    {
        await using var context = CreateContext(CreateRoleAwareGenerator(
            new Dictionary<string, Queue<Func<Task<string>>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["model-a"] = new Queue<Func<Task<string>>>([
                    () => DelayAndReturn("a-1", 10),
                    () => DelayAndReturn("a-2", 10),
                    () => DelayAndReturn("a-3", 10)
                ]),
                ["model-b"] = new Queue<Func<Task<string>>>([
                    () => DelayAndReturn("b-1", 5)
                ])
            }));

        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Planner, "model-a", "Benchmark prompt");
        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Planner, "model-a", "Benchmark prompt");
        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Planner, "model-a", "Benchmark prompt");
        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Planner, "model-b", "Benchmark prompt");

        var recommendations = await context.Service.RefreshAsync();
        var plannerRecommendation = Assert.Single(recommendations, item => item.AgentRole == AgentMode.Planner);

        Assert.True(plannerRecommendation.HasRecommendation);
        Assert.Equal("model-a", plannerRecommendation.RecommendedModel);
        Assert.True(plannerRecommendation.Score > 0);
        Assert.Equal(4, plannerRecommendation.SourceRunCount);

        var saved = await context.Service.GetLatestAsync();
        Assert.Equal(recommendations.Count, saved.Count);
        Assert.Equal(plannerRecommendation.RecommendedModel, saved.Single(item => item.AgentRole == AgentMode.Planner).RecommendedModel);
    }

    [Fact]
    public async Task RefreshAsync_ComparisonWinnerImprovesScore()
    {
        await using var context = CreateContext(CreateRoleAwareGenerator(
            new Dictionary<string, Queue<Func<Task<string>>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["model-a"] = new Queue<Func<Task<string>>>([
                    () => DelayAndReturn("a-benchmark", 5),
                    () => DelayAndReturn("a-comparison", 50)
                ]),
                ["model-b"] = new Queue<Func<Task<string>>>([
                    () => DelayAndReturn("b-benchmark", 50),
                    () => DelayAndReturn("b-comparison", 5)
                ])
            }));

        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.PatchBuilder, "model-a", "Benchmark prompt");
        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.PatchBuilder, "model-b", "Benchmark prompt");

        var comparison = await context.ComparisonService.RunComparisonAsync(
            AgentMode.PatchBuilder,
            ["model-a", "model-b"],
            "Compare prompt");

        Assert.Equal("model-b", comparison.BestModel);

        var recommendations = await context.Service.RefreshAsync();
        var patchBuilderRecommendation = Assert.Single(recommendations, item => item.AgentRole == AgentMode.PatchBuilder);

        Assert.True(patchBuilderRecommendation.HasRecommendation);
        Assert.Equal("model-b", patchBuilderRecommendation.RecommendedModel);
        Assert.Contains("comparison win", patchBuilderRecommendation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_FailedRunsReduceScore()
    {
        await using var context = CreateContext(CreateRoleAwareGenerator(
            new Dictionary<string, Queue<Func<Task<string>>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["model-a"] = new Queue<Func<Task<string>>>([
                    () => DelayAndReturn("a-success", 200),
                    () => Task.FromException<string>(new InvalidOperationException("boom"))
                ]),
                ["model-b"] = new Queue<Func<Task<string>>>([
                    () => DelayAndReturn("b-success", 5)
                ])
            }));

        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Reviewer, "model-a", "Benchmark prompt");
        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Reviewer, "model-a", "Benchmark prompt");
        await context.BenchmarkService.RunBenchmarkAsync(AgentMode.Reviewer, "model-b", "Benchmark prompt");

        var recommendations = await context.Service.RefreshAsync();
        var reviewerRecommendation = Assert.Single(recommendations, item => item.AgentRole == AgentMode.Reviewer);

        Assert.True(reviewerRecommendation.HasRecommendation);
        Assert.Equal("model-b", reviewerRecommendation.RecommendedModel);
        Assert.Contains("failed benchmark", reviewerRecommendation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_MissingDataReturnsClearEmptyResult()
    {
        await using var context = CreateContext(model => Task.FromResult("unused"));

        var recommendations = await context.Service.RefreshAsync();

        Assert.Equal(Enum.GetValues<AgentMode>().Length, recommendations.Count);
        Assert.All(recommendations, item =>
        {
            Assert.False(item.HasRecommendation);
            Assert.True(string.IsNullOrWhiteSpace(item.RecommendedModel));
            Assert.NotEmpty(item.Reason);
            Assert.Contains("No benchmark or comparison data", item.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static TestContextBundle CreateContext(Func<string, Task<string>> generateAsync)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-model-recommendation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var environment = new TestWebHostEnvironment(root);
        var ollamaService = Substitute.For<IOllamaService>();
        ollamaService.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => generateAsync(callInfo.ArgAt<string>(0)));

        var benchmarkService = new AgentModelBenchmarkService(ollamaService, environment);
        var comparisonService = new AgentModelComparisonService(benchmarkService, environment);
        var service = new AgentModelRecommendationService(environment);
        return new TestContextBundle(root, benchmarkService, comparisonService, service);
    }

    private static Func<string, Task<string>> CreateRoleAwareGenerator(Dictionary<string, Queue<Func<Task<string>>>> responses)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return modelName =>
        {
            if (!responses.TryGetValue(modelName, out var queue) || queue.Count == 0)
            {
                return Task.FromResult($"{modelName}-default");
            }

            counts.TryGetValue(modelName, out var count);
            counts[modelName] = count + 1;
            return queue.Dequeue().Invoke();
        };
    }

    private static async Task<string> DelayAndReturn(string text, int delayMs)
    {
        await Task.Delay(delayMs);
        return text;
    }

    private sealed record TestContextBundle(
        string Root,
        AgentModelBenchmarkService BenchmarkService,
        AgentModelComparisonService ComparisonService,
        AgentModelRecommendationService Service) : IAsyncDisposable
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
