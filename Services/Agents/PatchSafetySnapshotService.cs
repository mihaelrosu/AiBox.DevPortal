using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class PatchSafetySnapshotService(IWebHostEnvironment environment)
{
    private const string FileName = "patch-safety-snapshots.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<PatchSafetySnapshot>> GetLatestAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadSnapshotsAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchSafetySnapshot> CreateAsync(LocalCoderPatchPreview patchPreview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchPreview);

        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshot = new PatchSafetySnapshot
        {
            Id = snapshotId,
            CreatedAt = DateTimeOffset.UtcNow,
            PatchPreviewId = patchPreview.Id,
            SnapshotDirectory = GetSnapshotDirectory(snapshotId),
            TargetFiles = patchPreview.FileChanges
                .Select(change => NormalizeRelativePath(change.RelativePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(snapshot.SnapshotDirectory);

            var messages = new List<string>();
            var gitStatusResult = await RunGitStatusAsync(patchPreview.ProjectPath, cancellationToken);
            snapshot.GitStatus = gitStatusResult.Output.Trim();

            if (!gitStatusResult.Success)
            {
                snapshot.Success = false;
                snapshot.Message = gitStatusResult.Error.Trim();
                await SaveSnapshotAsync(snapshot, cancellationToken);
                return Clone(snapshot);
            }

            var missingFiles = new List<string>();
            foreach (var fileChange in patchPreview.FileChanges)
            {
                var relativePath = NormalizeRelativePath(fileChange.RelativePath);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var sourcePath = GetProjectPath(patchPreview.ProjectPath, relativePath);
                var destinationPath = GetSnapshotPath(snapshot.SnapshotDirectory, relativePath);

                if (!File.Exists(sourcePath))
                {
                    missingFiles.Add(relativePath);
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(sourcePath, destinationPath, overwrite: false);
            }

            snapshot.MissingTargetFiles = missingFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (missingFiles.Count > 0)
            {
                messages.Add($"Missing target files recorded: {string.Join(", ", missingFiles)}.");
            }

            snapshot.Success = true;
            snapshot.Message = messages.Count == 0
                ? "Patch safety snapshot created."
                : string.Join(" ", messages);
            await SaveSnapshotAsync(snapshot, cancellationToken);
            return Clone(snapshot);
        }
        catch (Exception exception)
        {
            snapshot.Success = false;
            snapshot.Message = exception.Message;
            await SaveSnapshotAsync(snapshot, cancellationToken);
            return Clone(snapshot);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchSafetySnapshotRestoreResult> RestoreAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return new PatchSafetySnapshotRestoreResult
            {
                Success = false,
                Message = "Snapshot id is required."
            };
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var snapshots = await LoadSnapshotsAsync(cancellationToken);
            var snapshot = snapshots.FirstOrDefault(item => string.Equals(item.Id, snapshotId, StringComparison.OrdinalIgnoreCase));

            if (snapshot is null)
            {
                return new PatchSafetySnapshotRestoreResult
                {
                    Success = false,
                    Message = $"Snapshot '{snapshotId}' was not found."
                };
            }

            if (string.IsNullOrWhiteSpace(snapshot.SnapshotDirectory) || !Directory.Exists(snapshot.SnapshotDirectory))
            {
                return new PatchSafetySnapshotRestoreResult
                {
                    Success = false,
                    Message = $"Snapshot '{snapshotId}' does not have a valid snapshot directory."
                };
            }

            var projectRoot = Path.GetFullPath(environment.ContentRootPath);
            var restoredFiles = new List<string>();
            var skippedFiles = new List<string>();
            var missingTargetFiles = new HashSet<string>(snapshot.MissingTargetFiles ?? [], StringComparer.OrdinalIgnoreCase);

            foreach (var relativePath in (snapshot.TargetFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalizedRelativePath = NormalizeRelativePath(relativePath);
                if (string.IsNullOrWhiteSpace(normalizedRelativePath))
                {
                    continue;
                }

                if (missingTargetFiles.Contains(normalizedRelativePath))
                {
                    skippedFiles.Add(normalizedRelativePath);
                    continue;
                }

                var snapshotFilePath = GetSnapshotPath(snapshot.SnapshotDirectory, normalizedRelativePath);
                if (!File.Exists(snapshotFilePath))
                {
                    skippedFiles.Add(normalizedRelativePath);
                    continue;
                }

                var projectFilePath = GetValidatedProjectPath(projectRoot, normalizedRelativePath);
                var destinationDirectory = Path.GetDirectoryName(projectFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                await CopyFileAsync(snapshotFilePath, projectFilePath, cancellationToken);
                restoredFiles.Add(normalizedRelativePath);
            }

            return new PatchSafetySnapshotRestoreResult
            {
                Success = true,
                Message = skippedFiles.Count == 0
                    ? $"Snapshot '{snapshotId}' restored."
                    : $"Snapshot '{snapshotId}' restored with {skippedFiles.Count} skipped file(s).",
                RestoredFiles = restoredFiles,
                SkippedFiles = skippedFiles
            };
        }
        catch (Exception exception)
        {
            return new PatchSafetySnapshotRestoreResult
            {
                Success = false,
                Message = exception.Message
            };
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<PatchSafetySnapshot>> LoadSnapshotsAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<PatchSafetySnapshot>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveSnapshotAsync(PatchSafetySnapshot snapshot, CancellationToken cancellationToken)
    {
        var items = await LoadSnapshotsAsync(cancellationToken);
        items.Add(Clone(snapshot));
        await SaveAsync(items, cancellationToken);
    }

    private async Task SaveAsync(List<PatchSafetySnapshot> items, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private async Task<(bool Success, string Output, string Error)> RunGitStatusAsync(string projectPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--short");

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                error.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            return (false, string.Empty, "Could not start git.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode == 0, output.ToString(), error.ToString());
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", FileName);
    }

    private string GetSnapshotDirectory(string snapshotId)
    {
        return Path.Combine(environment.ContentRootPath, "Data", "patch-safety-snapshots", snapshotId);
    }

    private static string GetSnapshotPath(string snapshotDirectory, string relativePath)
    {
        return Path.Combine(snapshotDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetProjectPath(string projectPath, string relativePath)
    {
        return Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string GetValidatedProjectPath(string projectPath, string relativePath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(GetProjectPath(fullProjectPath, relativePath));
        var rootPrefix = fullProjectPath + Path.DirectorySeparatorChar;

        if (!absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !absolutePath.Equals(fullProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Snapshot path '{relativePath}' is outside the project root.");
        }

        return absolutePath;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return (relativePath ?? string.Empty).Replace('\\', '/').Trim();
    }

    private static PatchSafetySnapshot Clone(PatchSafetySnapshot item)
    {
        return new PatchSafetySnapshot
        {
            Id = item.Id,
            CreatedAt = item.CreatedAt,
            PatchPreviewId = item.PatchPreviewId,
            GitStatus = item.GitStatus,
            TargetFiles = [.. (item.TargetFiles ?? [])],
            MissingTargetFiles = [.. (item.MissingTargetFiles ?? [])],
            SnapshotDirectory = item.SnapshotDirectory,
            Success = item.Success,
            Message = item.Message
        };
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }
}
