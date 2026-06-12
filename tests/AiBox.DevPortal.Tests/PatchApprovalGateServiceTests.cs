using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchApprovalGateServiceTests
{
    [Fact]
    public async Task EvaluateAsync_ContextFilesOnly_BlocksFilesOutsideContext()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "Components/Pages/Coder.razor", "page");
            await WriteFileAsync(root, "Services/AuthService.cs", "auth");

            var service = new PatchApprovalGateService();
            var results = await service.EvaluateAsync(new PatchPackage
            {
                ProjectPath = root,
                AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
                ContextFilePaths = ["Components/Pages/Coder.razor"],
                FileChanges =
                [
                    new PatchFileChange
                    {
                        RelativePath = "Services/AuthService.cs",
                        OldContent = "auth",
                        NewContent = "updated"
                    }
                ]
            });

            Assert.Contains(results, result =>
                result.GateKey == "patch-scope-context-files-only" &&
                !result.Passed &&
                result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateAsync_SelectedFolders_BlocksFilesOutsideAllowedFolders()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "Components/Pages/Coder.razor", "page");
            await WriteFileAsync(root, "Services/AuthService.cs", "auth");

            var service = new PatchApprovalGateService();
            var results = await service.EvaluateAsync(new PatchPackage
            {
                ProjectPath = root,
                AllowedPatchScope = PatchScopeMode.SelectedFolders,
                AllowedPatchFolders = ["Components/Pages/"],
                FileChanges =
                [
                    new PatchFileChange
                    {
                        RelativePath = "Services/AuthService.cs",
                        OldContent = "auth",
                        NewContent = "updated"
                    }
                ]
            });

            Assert.Contains(results, result =>
                result.GateKey == "patch-scope-selected-folders" &&
                !result.Passed &&
                result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateAsync_AnyProjectFile_WarnsWithoutBlocking()
    {
        var root = CreateRoot();

        try
        {
            await WriteFileAsync(root, "Services/AuthService.cs", "auth");

            var service = new PatchApprovalGateService();
            var results = await service.EvaluateAsync(new PatchPackage
            {
                ProjectPath = root,
                AllowedPatchScope = PatchScopeMode.AnyProjectFile,
                FileChanges =
                [
                    new PatchFileChange
                    {
                        RelativePath = "Services/AuthService.cs",
                        OldContent = "auth",
                        NewContent = "updated"
                    }
                ]
            });

            Assert.Contains(results, result =>
                result.GateKey == "patch-scope-warning" &&
                result.Warning &&
                result.Passed &&
                !result.Blocking);
            Assert.DoesNotContain(results, result => result.GateKey == "patch-scope-selected-folders" && result.Blocking);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"patch-scope-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteFileAsync(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }
}
