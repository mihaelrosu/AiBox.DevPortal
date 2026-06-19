using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModelBenchmarkServiceTests
{
    [Fact]
    public async Task RunBenchmarkAsync_SavesBenchmarkRun()
    {
        await using var context = CreateContext();

        var run = await context.Service.RunBenchmarkAsync(
            AgentMode.PatchBuilder,
            "benchmark-model",
            "Benchmark prompt");

        Assert.True(run.Success);
        Assert.Equal("benchmark-model", run.ModelName);
        Assert.Equal(AgentMode.PatchBuilder, run.AgentRole);

        var savedRuns = await context.Service.GetLatestAsync();
        Assert.Single(savedRuns);
        Assert.Equal(run.Id, savedRuns[0].Id);
        Assert.Equal("benchmark-model", savedRuns[0].ModelName);
    }

    [Fact]
    public async Task RunBenchmarkAsync_CalculatesDuration()
    {
        await using var context = CreateContext(async () =>
        {
            await Task.Delay(50);
            return "generated output";
        });

        var run = await context.Service.RunBenchmarkAsync(
            AgentMode.Verifier,
            "benchmark-model",
            "Benchmark prompt");

        Assert.True(run.Success);
        Assert.True(run.DurationMs > 0);
        Assert.Equal("generated output".Length, run.OutputLength);
    }

    [Fact]
    public async Task RunBenchmarkAsync_RecordsFailedBenchmark()
    {
        await using var context = CreateContext(() => Task.FromException<string>(new InvalidOperationException("boom")));

        var run = await context.Service.RunBenchmarkAsync(
            AgentMode.Reviewer,
            "benchmark-model",
            "Benchmark prompt");

        Assert.False(run.Success);
        Assert.Equal(0, run.OutputLength);
        Assert.Contains("boom", run.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var savedRuns = await context.Service.GetLatestAsync();
        Assert.Single(savedRuns);
        Assert.False(savedRuns[0].Success);
        Assert.Contains("boom", savedRuns[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static TestContextBundle CreateContext(Func<Task<string>>? generateAsync = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-model-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var environment = new TestWebHostEnvironment(root);
        var ollamaService = Substitute.For<IOllamaService>();
        ollamaService.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => generateAsync?.Invoke() ?? Task.FromResult("benchmark output"));

        var service = new AgentModelBenchmarkService(ollamaService, environment);
        return new TestContextBundle(root, service);
    }

    private sealed record TestContextBundle(string Root, AgentModelBenchmarkService Service) : IAsyncDisposable
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
