using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PatchRollbackService(
    IPatchPackageService patchPackageService,
    IWebHostEnvironment environment) : IPatchRollbackService
{
    private const string HistoryFileName = "patch-rollback.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<PatchRollbackEntry> RecordAsync(PatchRollbackEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            var saved = Clone(entry);

            if (string.IsNullOrWhiteSpace(saved.Id))
            {
                saved.Id = Guid.NewGuid().ToString("N");
            }

            if (saved.TimestampUtc == default)
            {
                saved.TimestampUtc = DateTime.UtcNow;
            }

            var index = entries.FindIndex(item => item.BackupId.Equals(saved.BackupId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries[index] = saved;
            }
            else
            {
                entries.Add(saved);
            }

            await SaveAsync(entries, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<PatchRollbackEntry>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.TimestampUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

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

        var restoredFiles = new List<string>();
        try
        {
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

            foreach (var backupFile in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

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

            await UpdateRollbackEntryAsync(package.BackupFolder, true, DateTime.UtcNow, restoredFiles, cancellationToken);
            return await patchPackageService.SaveAsync(package, cancellationToken);
        }
        catch (Exception exception)
        {
            package.RollbackResult = string.Empty;
            package.RollbackError = exception.Message;
            package.StatusMessage = exception.Message;
            await patchPackageService.SaveAsync(package, cancellationToken);
            await UpdateRollbackEntryAsync(package.BackupFolder, false, DateTime.UtcNow, restoredFiles, cancellationToken);
            throw;
        }
    }

    private async Task UpdateRollbackEntryAsync(
        string backupId,
        bool restored,
        DateTime restoreTimestampUtc,
        IReadOnlyList<string> restoredFiles,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadAsync(cancellationToken);
            var index = entries.FindIndex(item => item.BackupId.Equals(backupId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var entry = entries[index];
            entry.Restored = restored;
            entry.RestoreTimestampUtc = restoreTimestampUtc;
            if (restoredFiles.Count > 0)
            {
                entry.ChangedFiles = [.. restoredFiles];
            }

            entries[index] = entry;
            await SaveAsync(entries, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<PatchRollbackEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<PatchRollbackEntry>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<PatchRollbackEntry> entries, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", HistoryFileName);
    }

    private static PatchRollbackEntry Clone(PatchRollbackEntry item)
    {
        return new PatchRollbackEntry
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            PlanId = item.PlanId,
            SliceId = item.SliceId,
            BackupId = item.BackupId,
            ChangedFiles = [.. item.ChangedFiles ?? []],
            Restored = item.Restored,
            RestoreTimestampUtc = item.RestoreTimestampUtc
        };
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
