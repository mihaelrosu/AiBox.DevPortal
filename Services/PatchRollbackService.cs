using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PatchRollbackService(
    IPatchPackageService patchPackageService) : IPatchRollbackService
{
    public async Task<PatchPackage> RollbackAsync(string patchPackageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patchPackageId))
        {
            throw new InvalidOperationException("Patch package id is required.");
        }

        var package = await patchPackageService.GetByIdAsync(patchPackageId, cancellationToken)
            ?? throw new InvalidOperationException($"Patch package not found: {patchPackageId}");

        if (package.Status != PatchPackageStatus.Applied || package.RolledBack)
        {
            throw new InvalidOperationException("Only applied patches that have not already been rolled back can be restored.");
        }

        if (string.IsNullOrWhiteSpace(package.BackupFolder))
        {
            throw new InvalidOperationException("Patch package does not contain a backup folder.");
        }

        var rootPath = GetValidatedProjectRoot(package.ProjectPath);
        var backupRoot = ResolveBackupRoot(rootPath, package.BackupFolder);
        if (!Directory.Exists(backupRoot))
        {
            throw new InvalidOperationException($"Backup folder does not exist: {package.BackupFolder}");
        }

        try
        {
            var restoredFiles = new List<string>();

            foreach (var backupFile in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
            {
                var relativeToBackup = Path.GetRelativePath(backupRoot, backupFile);
                var targetPath = GetValidatedTargetPath(rootPath, relativeToBackup);
                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                await using var source = File.OpenRead(backupFile);
                await using var target = File.Create(targetPath);
                await source.CopyToAsync(target, cancellationToken);
                restoredFiles.Add(relativeToBackup.Replace('\\', '/'));
            }

            package.RolledBack = true;
            package.RolledBackAt = DateTimeOffset.UtcNow;
            package.RollbackResult = $"Restored {restoredFiles.Count} file(s) from {package.BackupFolder}.";
            package.RollbackError = string.Empty;
            package.Status = PatchPackageStatus.RolledBack;
            package.StatusMessage = package.RollbackResult;

            return await patchPackageService.SaveAsync(package, cancellationToken);
        }
        catch (Exception exception)
        {
            package.RollbackResult = string.Empty;
            package.RollbackError = exception.Message;
            package.StatusMessage = exception.Message;
            await patchPackageService.SaveAsync(package, cancellationToken);
            throw;
        }
    }

    private static string ResolveBackupRoot(string rootPath, string backupFolder)
    {
        var normalized = backupFolder.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        return Path.GetFullPath(Path.Combine(rootPath, normalized));
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
            throw new InvalidOperationException($"Invalid backup file path: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Backup file path is outside the project root: {relativePath}");
        }

        return fullPath;
    }
}
