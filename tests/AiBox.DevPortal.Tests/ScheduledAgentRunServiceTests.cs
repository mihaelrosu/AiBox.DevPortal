using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ScheduledAgentRunServiceTests
{
    [Fact]
    public async Task CreateAsync_RejectsMissingName()
    {
        await AssertInvalidScheduleAsync(
            schedule => schedule.Name = string.Empty,
            "Schedule name is required.");
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingTaskName()
    {
        await AssertInvalidScheduleAsync(
            schedule => schedule.TaskName = " ",
            "Task name is required.");
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingRequest()
    {
        await AssertInvalidScheduleAsync(
            schedule => schedule.UserRequest = string.Empty,
            "User request is required.");
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidPolicy()
    {
        await AssertInvalidScheduleAsync(
            schedule => schedule.ExecutionPolicyName = "DoesNotExist",
            "Execution policy 'DoesNotExist' does not exist.");
    }

    [Fact]
    public async Task CreateAsync_PersistsValidSchedule()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);

            var created = await service.CreateAsync(new ScheduledAgentRun
            {
                Name = "Nightly run",
                Enabled = true,
                CronExpression = "0 0 * * *",
                TaskName = "Implement orchestration",
                UserRequest = "Create a nightly orchestration run.",
                ExecutionPolicyName = "Safe",
                CommitAndSync = false
            });

            Assert.False(string.IsNullOrWhiteSpace(created.Id));
            Assert.Equal("Nightly run", created.Name);
            Assert.True(created.Enabled);
            Assert.Equal("0 0 * * *", created.CronExpression);
            Assert.Equal("Implement orchestration", created.TaskName);
            Assert.Equal("Create a nightly orchestration run.", created.UserRequest);
            Assert.Equal("Safe", created.ExecutionPolicyName);
            Assert.False(created.CommitAndSync);
            Assert.True(created.CreatedAtUtc != default);
            Assert.True(File.Exists(Path.Combine(root, "Data", "scheduled-agent-runs.json")));

            var schedules = await service.GetAllAsync();
            var saved = Assert.Single(schedules);
            Assert.Equal(created.Id, saved.Id);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task UpdateAsync_UpdatesSchedule()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var created = await service.CreateAsync(CreateValidSchedule("Safe"));

            var updated = await service.UpdateAsync(created.Id, new ScheduledAgentRun
            {
                Name = "Updated run",
                Enabled = false,
                CronExpression = "30 1 * * 1-5",
                TaskName = "Updated task",
                UserRequest = "Updated request",
                ExecutionPolicyName = "Balanced",
                CommitAndSync = true,
                LastRunAtUtc = DateTime.UtcNow.AddHours(-1),
                NextRunAtUtc = DateTime.UtcNow.AddHours(1)
            });

            Assert.NotNull(updated);
            Assert.Equal(created.Id, updated!.Id);
            Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
            Assert.Equal("Updated run", updated.Name);
            Assert.False(updated.Enabled);
            Assert.Equal("30 1 * * 1-5", updated.CronExpression);
            Assert.Equal("Updated task", updated.TaskName);
            Assert.Equal("Updated request", updated.UserRequest);
            Assert.Equal("Balanced", updated.ExecutionPolicyName);
            Assert.True(updated.CommitAndSync);
            Assert.NotNull(updated.LastRunAtUtc);
            Assert.NotNull(updated.NextRunAtUtc);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task EnableDisableAsync_TogglesSchedule()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var created = await service.CreateAsync(CreateValidSchedule("Safe", enabled: false));

            var enabled = await service.EnableAsync(created.Id);
            Assert.NotNull(enabled);
            Assert.True(enabled!.Enabled);

            var disabled = await service.DisableAsync(created.Id);
            Assert.NotNull(disabled);
            Assert.False(disabled!.Enabled);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesSchedule()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var created = await service.CreateAsync(CreateValidSchedule("Safe"));

            var deleted = await service.DeleteAsync(created.Id);

            Assert.True(deleted);
            Assert.Empty(await service.GetAllAsync());
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static async Task AssertInvalidScheduleAsync(Action<ScheduledAgentRun> mutate, string expectedMessage)
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var schedule = CreateValidSchedule("Safe");
            mutate(schedule);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(schedule));

            Assert.Equal(expectedMessage, exception.Message);
            Assert.Empty(await service.GetAllAsync());
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static ScheduledAgentRun CreateValidSchedule(string policyName, bool enabled = true)
    {
        return new ScheduledAgentRun
        {
            Name = "Nightly run",
            Enabled = enabled,
            CronExpression = "0 0 * * *",
            TaskName = "Implement orchestration",
            UserRequest = "Create a nightly orchestration run.",
            ExecutionPolicyName = policyName,
            CommitAndSync = false
        };
    }

    private static ScheduledAgentRunService CreateService(string root)
    {
        var environment = new TestWebHostEnvironment(root);
        var policyService = new ExecutionPolicyProfileService(environment);
        return new ScheduledAgentRunService(environment, policyService);
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scheduled-agent-runs-{Guid.NewGuid():N}");
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
