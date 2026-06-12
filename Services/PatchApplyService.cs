using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PatchApplyService(
    IPatchPackageService patchPackageService) : IPatchApplyService
{
    public async Task<PatchPackage> ApplyAsync(string patchPackageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patchPackageId))
        {
            throw new InvalidOperationException("Patch package id is required.");
        }

        var package = await patchPackageService.GetByIdAsync(patchPackageId, cancellationToken)
            ?? throw new InvalidOperationException($"Patch package not found: {patchPackageId}");

        PatchScopeGuard.ThrowIfBlocking(package);
        PatchIntentGuard.ThrowIfBlocking(package);

        if (package.Status != PatchPackageStatus.Approved)
        {
            throw new InvalidOperationException("Only approved patches can be applied.");
        }

        if (!package.ApprovalGatesPassed)
        {
            throw new InvalidOperationException("Approved patches must pass all approval gates before they can be applied.");
        }

        var rootPath = GetValidatedProjectRoot(package.ProjectPath);
        var backupRoot = CreateBackupRoot(rootPath);
        var backupFiles = new List<string>();

        foreach (var change in package.FileChanges)
        {
            var targetPath = GetValidatedTargetPath(rootPath, change.RelativePath);
            var relativePath = Path.GetRelativePath(rootPath, targetPath).Replace('\\', '/');

            if (File.Exists(targetPath))
            {
                var backupPath = Path.Combine(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                await File.WriteAllBytesAsync(backupPath, await File.ReadAllBytesAsync(targetPath, cancellationToken), cancellationToken);
                backupFiles.Add(Path.GetRelativePath(rootPath, backupPath).Replace('\\', '/'));
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await File.WriteAllTextAsync(targetPath, change.NewContent ?? string.Empty, new UTF8Encoding(false), cancellationToken);
        }

        package.BackupFolder = Path.GetRelativePath(rootPath, backupRoot).Replace('\\', '/');
        package.AppliedAt = DateTimeOffset.UtcNow;
        package.RolledBack = false;
        package.RolledBackAt = null;
        package.RollbackResult = string.Empty;
        package.RollbackError = string.Empty;
        package.Status = PatchPackageStatus.Applied;
        package.StatusMessage = $"Applied with {backupFiles.Count} backup file(s).";

        return await patchPackageService.SaveAsync(package, cancellationToken);
    }

    private static string CreateBackupRoot(string projectPath)
    {
        var root = Path.Combine(projectPath, "Data", "patch-backups", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetValidatedProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException("Project path is required.");
        }

        var fullPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Project path does not exist: {fullPath}");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string GetValidatedTargetPath(string rootPath, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid patch file path: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Patch file path is outside the project root: {relativePath}");
        }

        return fullPath;
    }
}
