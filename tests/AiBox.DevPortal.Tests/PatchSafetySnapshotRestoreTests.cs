using System.Diagnostics;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchSafetySnapshotRestoreTests
{
    [Fact]
    public async Task RestoreAsync_CopiesPreviousFileContentBack()
    {
        var root = CreateTempProjectRoot();

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Source"));
            await File.WriteAllTextAsync(Path.Combine(root, "Source", "Existing.cs"), "before");
            InitializeGitRepository(root);

            var service = new PatchSafetySnapshotService(new TestWebHostEnvironment(root));
            var preview = new LocalCoderPatchPreview
            {
                Id = "preview-restore",
                ProjectPath = root,
                PatchText = "diff --git a/Source/Existing.cs b/Source/Existing.cs",
                FileChanges =
                [
                    new PatchFileChange
                    {
                        RelativePath = "Source/Existing.cs",
                        OldContent = "before",
                        NewContent = "after"
                    }
                ]
            };

            var snapshot = await service.CreateAsync(preview);
            await File.WriteAllTextAsync(Path.Combine(root, "Source", "Existing.cs"), "after");

            var result = await service.RestoreAsync(snapshot.Id);

            Assert.True(result.Success);
            Assert.Contains("Source/Existing.cs", result.RestoredFiles);
            Assert.Empty(result.SkippedFiles);
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(root, "Source", "Existing.cs")));
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RestoreAsync_MissingCreatedFilesAreSkipped()
    {
        var root = CreateTempProjectRoot();

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Source"));
            await File.WriteAllTextAsync(Path.Combine(root, "Source", "Existing.cs"), "before");
            InitializeGitRepository(root);

            var service = new PatchSafetySnapshotService(new TestWebHostEnvironment(root));
            var preview = new LocalCoderPatchPreview
            {
                Id = "preview-missing-create",
                ProjectPath = root,
                PatchText = "diff --git a/Source/Existing.cs b/Source/Existing.cs",
                FileChanges =
                [
                    new PatchFileChange
                    {
                        RelativePath = "Source/Existing.cs",
                        OldContent = "before",
                        NewContent = "after"
                    },
                    new PatchFileChange
                    {
                        RelativePath = "Source/Created.cs",
                        OldContent = string.Empty,
                        NewContent = "new file"
                    }
                ]
            };

            var snapshot = await service.CreateAsync(preview);
            await File.WriteAllTextAsync(Path.Combine(root, "Source", "Existing.cs"), "after");

            var result = await service.RestoreAsync(snapshot.Id);

            Assert.True(result.Success);
            Assert.Contains("Source/Existing.cs", result.RestoredFiles);
            Assert.Contains("Source/Created.cs", result.SkippedFiles);
            Assert.False(File.Exists(Path.Combine(root, "Source", "Created.cs")));
            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(root, "Source", "Existing.cs")));
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RestoreAsync_UnknownSnapshotId_ReturnsClearFailure()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = new PatchSafetySnapshotService(new TestWebHostEnvironment(root));

            var result = await service.RestoreAsync("missing-snapshot");

            Assert.False(result.Success);
            Assert.Contains("missing-snapshot", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.RestoredFiles);
            Assert.Empty(result.SkippedFiles);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"patch-safety-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void InitializeGitRepository(string root)
    {
        RunGitCommand(root, ["init"]);
        RunGitCommand(root, ["checkout", "-b", "main"]);
        RunGitCommand(root, ["config", "user.email", "test@example.com"]);
        RunGitCommand(root, ["config", "user.name", "Test User"]);
        RunGitCommand(root, ["add", "-A"]);
        RunGitCommand(root, ["commit", "-m", "Initial baseline"]);
    }

    private static void RunGitCommand(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git process.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed: git {string.Join(" ", arguments)}{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
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
