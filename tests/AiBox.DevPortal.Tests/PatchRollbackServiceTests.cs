using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchRollbackServiceTests
{
    [Fact]
    public async Task RollbackAsync_RestoresFilesFromBackupFolder()
    {
        await using var context = CreateContext("RollbackAsync_RestoresFilesFromBackupFolder");

        var backupResult = await context.BackupService.CreateBackupAsync(
            context.ProjectRoot,
            ["Program.cs"]);

        await File.WriteAllTextAsync(Path.Combine(context.ProjectRoot, "Program.cs"), """
            Console.WriteLine("Changed");
            """);

        var package = await context.PatchPackageService.CreateFromChangesAsync(
            context.ProjectRoot,
            "rollback test",
            "test-model",
            "patch text",
            [
                new PatchFileChange
                {
                    RelativePath = "Program.cs",
                    OldContent = """
                        Console.WriteLine("Initial");
                        """,
                    NewContent = """
                        Console.WriteLine("Changed");
                        """
                }
            ]);

        package.BackupFolder = backupResult.BackupFolderPath;
        package.Status = PatchPackageStatus.Applied;
        package.StatusMessage = "Applied";
        await context.PatchPackageService.SaveAsync(package);

        await context.RollbackService.RecordAsync(new PatchRollbackEntry
        {
            PlanId = "plan-1",
            SliceId = "slice-1",
            BackupId = backupResult.BackupFolderPath,
            ChangedFiles = ["Program.cs"]
        });

        var rolledBack = await context.RollbackService.RollbackAsync(package.Id);

        Assert.Equal(PatchPackageStatus.RolledBack, rolledBack.Status);
        Assert.True(rolledBack.RolledBack);
        Assert.NotNull(rolledBack.RolledBackAt);

        var restoredText = await File.ReadAllTextAsync(Path.Combine(context.ProjectRoot, "Program.cs"));
        Assert.Contains("Initial", restoredText, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed", restoredText, StringComparison.Ordinal);

        var rollbackEntries = await context.RollbackService.GetLatestAsync(10);
        var entry = Assert.Single(rollbackEntries);
        Assert.True(entry.Restored);
        Assert.NotNull(entry.RestoreTimestampUtc);
        Assert.Equal(backupResult.BackupFolderPath, entry.BackupId);
        Assert.Contains("Program.cs", entry.ChangedFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackAsync_HandlesMissingBackupFolder()
    {
        await using var context = CreateContext("RollbackAsync_HandlesMissingBackupFolder");

        var backupResult = await context.BackupService.CreateBackupAsync(
            context.ProjectRoot,
            ["Program.cs"]);

        Directory.Delete(backupResult.BackupFolderPath, recursive: true);

        var package = await context.PatchPackageService.CreateFromChangesAsync(
            context.ProjectRoot,
            "rollback missing backup",
            "test-model",
            "patch text",
            [
                new PatchFileChange
                {
                    RelativePath = "Program.cs",
                    OldContent = """
                        Console.WriteLine("Initial");
                        """,
                    NewContent = """
                        Console.WriteLine("Changed");
                        """
                }
            ]);

        package.BackupFolder = backupResult.BackupFolderPath;
        package.Status = PatchPackageStatus.Applied;
        package.StatusMessage = "Applied";
        await context.PatchPackageService.SaveAsync(package);

        await context.RollbackService.RecordAsync(new PatchRollbackEntry
        {
            PlanId = "plan-1",
            SliceId = "slice-1",
            BackupId = backupResult.BackupFolderPath,
            ChangedFiles = ["Program.cs"]
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.RollbackService.RollbackAsync(package.Id));
        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);

        var rollbackEntries = await context.RollbackService.GetLatestAsync(10);
        var entry = Assert.Single(rollbackEntries);
        Assert.False(entry.Restored);
        Assert.NotNull(entry.RestoreTimestampUtc);
        Assert.Equal(backupResult.BackupFolderPath, entry.BackupId);
    }

    [Fact]
    public async Task RollbackAsync_UpdatesRollbackAuditEntry()
    {
        await using var context = CreateContext("RollbackAsync_UpdatesRollbackAuditEntry");

        var backupResult = await context.BackupService.CreateBackupAsync(
            context.ProjectRoot,
            ["Program.cs"]);

        var package = await context.PatchPackageService.CreateFromChangesAsync(
            context.ProjectRoot,
            "rollback audit",
            "test-model",
            "patch text",
            [
                new PatchFileChange
                {
                    RelativePath = "Program.cs",
                    OldContent = """
                        Console.WriteLine("Initial");
                        """,
                    NewContent = """
                        Console.WriteLine("Changed");
                        """
                }
            ]);

        package.BackupFolder = backupResult.BackupFolderPath;
        package.Status = PatchPackageStatus.Applied;
        package.StatusMessage = "Applied";
        await context.PatchPackageService.SaveAsync(package);

        await context.RollbackService.RecordAsync(new PatchRollbackEntry
        {
            PlanId = "plan-1",
            SliceId = "slice-1",
            BackupId = backupResult.BackupFolderPath,
            ChangedFiles = ["Program.cs"]
        });

        await context.RollbackService.RollbackAsync(package.Id);

        var entry = Assert.Single(await context.RollbackService.GetLatestAsync(10));
        Assert.True(entry.Restored);
        Assert.NotNull(entry.RestoreTimestampUtc);
        Assert.Equal("plan-1", entry.PlanId);
        Assert.Equal("slice-1", entry.SliceId);
        Assert.Equal(backupResult.BackupFolderPath, entry.BackupId);
        Assert.Single(entry.ChangedFiles);
    }

    private static TestContextBundle CreateContext(string testName)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"patch-rollback-{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);

        File.WriteAllText(
            Path.Combine(projectRoot, "Program.cs"),
            """
            Console.WriteLine("Initial");
            """);

        var environment = new TestWebHostEnvironment(projectRoot);
        var approvalGateService = Substitute.For<IPatchApprovalGateService>();
        approvalGateService.EvaluateAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PatchApprovalGateResult>>([]));

        var patchPackageService = new PatchPackageService(environment, approvalGateService);
        var rollbackService = new PatchRollbackService(patchPackageService, environment);
        var backupService = new PatchBackupService(environment, Substitute.For<ILogger<PatchBackupService>>());

        return new TestContextBundle(projectRoot, environment, patchPackageService, rollbackService, backupService);
    }

    private sealed record TestContextBundle(
        string ProjectRoot,
        IWebHostEnvironment Environment,
        PatchPackageService PatchPackageService,
        PatchRollbackService RollbackService,
        PatchBackupService BackupService) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(ProjectRoot))
            {
                Directory.Delete(ProjectRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }
}
