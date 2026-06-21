using System.Reflection;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ScheduledRunDispatcherTests
{
    [Fact]
    public async Task ProcessDueRunsAsync_CreatesAndCompletesExecutionRecord()
    {
        var root = CreateTempProjectRoot();

        try
        {
            using var provider = CreateServiceProvider(root);
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var dispatcher = new ScheduledRunDispatcher(scopeFactory, NullLogger<ScheduledRunDispatcher>.Instance);

            ScheduledAgentRun createdRun;
            using (var scope = provider.CreateScope())
            {
                var runService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
                createdRun = await runService.CreateAsync(new ScheduledAgentRun
                {
                    Name = "Nightly dispatcher run",
                    Enabled = true,
                    CronExpression = "0 0 * * *",
                    TaskName = "Verify dispatcher execution",
                    UserRequest = "Run dispatcher once.",
                    ExecutionPolicyName = "Safe",
                    NextRunUtc = DateTime.UtcNow.AddMinutes(-5)
                });
            }

            await InvokeProcessDueRunsAsync(dispatcher);

            using (var scope = provider.CreateScope())
            {
                var runService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
                var executionService = scope.ServiceProvider.GetRequiredService<ScheduledAgentExecutionService>();

                var updatedRun = await runService.GetByIdAsync(createdRun.Id);
                Assert.NotNull(updatedRun);
                Assert.False(updatedRun!.IsRunning);
                Assert.NotNull(updatedRun.LastStartedUtc);
                Assert.NotNull(updatedRun.LastCompletedUtc);

                var executions = await executionService.GetByRunIdAsync(createdRun.Id);
                var execution = Assert.Single(executions);
                Assert.Equal(createdRun.Id, execution.ScheduledRunId);
                Assert.Equal(createdRun.Name, execution.ScheduleName);
                Assert.Equal(createdRun.TaskName, execution.TaskName);
                Assert.True(execution.Success);
                Assert.NotNull(execution.CompletedUtc);
                Assert.Null(execution.Error);
            }
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static async Task InvokeProcessDueRunsAsync(ScheduledRunDispatcher dispatcher)
    {
        var method = typeof(ScheduledRunDispatcher).GetMethod("ProcessDueRunsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method!.Invoke(dispatcher, new object[] { CancellationToken.None });
        Assert.NotNull(task);
        await task!;
    }

    private static ServiceProvider CreateServiceProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(root));
        services.AddScoped<ExecutionPolicyProfileService>();
        services.AddScoped<ScheduledAgentRunService>();
        services.AddScoped<ScheduledAgentExecutionService>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<ScheduledRunDispatcher>), NullLogger<ScheduledRunDispatcher>.Instance);
        return services.BuildServiceProvider();
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scheduled-run-dispatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
