using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ScheduledAgentExecutionServiceTests
{
    [Fact]
    public async Task CreateAsync_PersistsExecution()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var runId = Guid.NewGuid().ToString("N");

            var created = await service.CreateAsync(new ScheduledAgentExecution
            {
                ScheduledRunId = runId,
                ScheduleName = "Nightly run",
                TaskName = "Implement orchestration",
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                CompletedUtc = DateTimeOffset.UtcNow,
                Success = true,
                DurationMs = 120000
            });

            Assert.False(string.IsNullOrWhiteSpace(created.Id));
            Assert.Equal(runId, created.ScheduledRunId);
            Assert.Equal("Nightly run", created.ScheduleName);
            Assert.Equal("Implement orchestration", created.TaskName);
            Assert.True(File.Exists(Path.Combine(root, "Data", "scheduled-agent-executions.json")));

            var executions = await service.GetAllAsync();
            var saved = Assert.Single(executions);
            Assert.Equal(created.Id, saved.Id);
            Assert.Equal(created.ScheduledRunId, saved.ScheduledRunId);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task GetAllAsync_ReturnsStoredExecutions()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var execution1 = await service.CreateAsync(CreateValidExecution(Guid.NewGuid().ToString("N")));
            var execution2 = await service.CreateAsync(CreateValidExecution(Guid.NewGuid().ToString("N")));

            var executions = await service.GetAllAsync();

            Assert.Equal(2, executions.Count);
            Assert.Equal(execution1.Id, executions[0].Id);
            Assert.Equal(execution2.Id, executions[1].Id);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task GetByRunIdAsync_FiltersByScheduledRunId()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var runId1 = Guid.NewGuid().ToString("N");
            var runId2 = Guid.NewGuid().ToString("N");

            var execution1 = await service.CreateAsync(CreateValidExecution(runId1));
            var execution2 = await service.CreateAsync(CreateValidExecution(runId2));

            var executionsForRun1 = await service.GetByRunIdAsync(runId1);
            Assert.Single(executionsForRun1);
            Assert.Equal(execution1.Id, executionsForRun1[0].Id);

            var executionsForRun2 = await service.GetByRunIdAsync(runId2);
            Assert.Single(executionsForRun2);
            Assert.Equal(execution2.Id, executionsForRun2[0].Id);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task PersistedExecution_SurvivesCreatingNewServiceInstance()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service1 = CreateService(root);
            var created = await service1.CreateAsync(CreateValidExecution(Guid.NewGuid().ToString("N")));

            var executions1 = await service1.GetAllAsync();
            Assert.Single(executions1);
            Assert.Equal(created.Id, executions1[0].Id);

            var service2 = CreateService(root);
            var executions2 = await service2.GetAllAsync();
            Assert.Single(executions2);
            Assert.Equal(created.Id, executions2[0].Id);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static ScheduledAgentExecution CreateValidExecution(string runId)
    {
        return new ScheduledAgentExecution
        {
            ScheduledRunId = runId,
            ScheduleName = "Nightly run",
            TaskName = "Implement orchestration",
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedUtc = DateTimeOffset.UtcNow,
            Success = true,
            DurationMs = 300000
        };
    }

    private static ScheduledAgentExecutionService CreateService(string root)
    {
        var environment = new TestWebHostEnvironment(root);
        return new ScheduledAgentExecutionService(environment);
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scheduled-agent-executions-{Guid.NewGuid():N}");
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
