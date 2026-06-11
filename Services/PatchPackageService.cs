using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services;

public sealed class PatchPackageService(
    IWebHostEnvironment environment,
    IPatchApprovalGateService patchApprovalGateService) : IPatchPackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<PatchPackage>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var packages = await LoadAllAsync(cancellationToken);
            return packages
                .OrderByDescending(package => package.UpdatedAt)
                .ThenByDescending(package => package.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchPackage?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var path = GetPackagePath(id);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var package = await JsonSerializer.DeserializeAsync<PatchPackage>(stream, JsonOptions, cancellationToken);
            return package is null ? null : Clone(package);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchPackage> CreateFromPreviewAsync(LocalCoderPatchPreview preview, string userRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        if (string.IsNullOrWhiteSpace(preview.PatchText) ||
            preview.PatchText.Contains("PATCH_NOT_POSSIBLE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Patch preview does not contain an applicable patch.");
        }

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var package = BuildPackage(
                preview.ProjectPath,
                userRequest,
                preview.Model,
                preview.PatchText,
                BuildFileChanges(preview));
            await SaveInternalAsync(package, cancellationToken);
            return Clone(package);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchPackage> CreateFromChangesAsync(
        string projectPath,
        string userRequest,
        string model,
        string patchText,
        IReadOnlyList<PatchFileChange> fileChanges,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patchText) ||
            patchText.Contains("PATCH_NOT_POSSIBLE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Patch preview does not contain an applicable patch.");
        }

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var package = BuildPackage(projectPath, userRequest, model, patchText, fileChanges);
            await SaveInternalAsync(package, cancellationToken);
            return Clone(package);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchPackage> SaveAsync(PatchPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var saved = Clone(package);
            saved.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveInternalAsync(saved, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<PatchPackage?> UpdateStatusAsync(string id, PatchPackageStatus status, string? statusMessage = null, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var package = await LoadPackageAsync(id, cancellationToken);
            if (package is null)
            {
                return null;
            }

            package.Status = status;
            package.StatusMessage = statusMessage ?? string.Empty;
            package.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveInternalAsync(package, cancellationToken);
            return Clone(package);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public Task<PatchPackage?> ApproveAsync(string id, CancellationToken cancellationToken = default)
    {
        return ApproveWithGatesAsync(id, cancellationToken);
    }

    private async Task<PatchPackage?> ApproveWithGatesAsync(string id, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var package = await LoadPackageAsync(id, cancellationToken);
            if (package is null)
            {
                return null;
            }

            var gateResults = await patchApprovalGateService.EvaluateAsync(package, cancellationToken);
            package.ApprovalGateResults = gateResults;

            var blockingFailures = gateResults.Count(result => result.Blocking && !result.Passed);
            if (blockingFailures == 0)
            {
                package.Status = PatchPackageStatus.Approved;
                package.StatusMessage = "Patch approved.";
            }
            else
            {
                package.Status = PatchPackageStatus.Reviewed;
                package.StatusMessage = $"Approval gates failed with {blockingFailures} blocking issue(s).";
            }

            package.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveInternalAsync(package, cancellationToken);
            return Clone(package);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<PatchPackage>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var directory = GetPackagesDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<PatchPackage>();
        foreach (var filePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var stream = File.OpenRead(filePath);
            var package = await JsonSerializer.DeserializeAsync<PatchPackage>(stream, JsonOptions, cancellationToken);
            if (package is not null)
            {
                result.Add(package);
            }
        }

        return result;
    }

    private async Task<PatchPackage?> LoadPackageAsync(string id, CancellationToken cancellationToken)
    {
        var path = GetPackagePath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PatchPackage>(stream, JsonOptions, cancellationToken);
    }

    private async Task SaveInternalAsync(PatchPackage package, CancellationToken cancellationToken)
    {
        var path = GetPackagePath(package.Id);
        Directory.CreateDirectory(GetPackagesDirectory());

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, package, JsonOptions, cancellationToken);
    }

    private string GetPackagesDirectory()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "patches");
    }

    private string GetPackagePath(string id)
    {
        return Path.Combine(GetPackagesDirectory(), $"{id}.json");
    }

    private static PatchPackage BuildPackage(
        string projectPath,
        string userRequest,
        string model,
        string patchText,
        IReadOnlyList<PatchFileChange> fileChanges)
    {
        return new PatchPackage
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ProjectPath = projectPath,
            UserRequest = userRequest ?? string.Empty,
            Model = model,
            PatchText = patchText,
            Status = PatchPackageStatus.Draft,
            StatusMessage = "Draft patch package created from preview.",
            FileChanges = fileChanges.Select(Clone).ToArray()
        };
    }

    private static IReadOnlyList<PatchFileChange> BuildFileChanges(LocalCoderPatchPreview preview)
    {
        if (preview.FileChanges is { Count: > 0 })
        {
            return preview.FileChanges.Select(Clone).ToArray();
        }

        var fileContexts = preview.FileContexts
            .ToDictionary(context => NormalizePath(context.RelativePath), context => context.Content, StringComparer.OrdinalIgnoreCase);

        var lines = preview.PatchText.ReplaceLineEndings("\n").Split('\n');
        var changes = new List<PatchFileChange>();
        var index = 0;

        while (index < lines.Length)
        {
            if (!lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var header = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length < 4)
            {
                throw new InvalidOperationException("Patch package parsing failed: invalid diff header.");
            }

            var relativePath = NormalizePath(StripDiffPathPrefix(header[3]));
            index++;

            var sectionLines = new List<string>();
            while (index < lines.Length && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                sectionLines.Add(lines[index]);
                index++;
            }

            var oldContent = fileContexts.GetValueOrDefault(relativePath) ?? string.Empty;
            var newContent = ApplyDiffSection(oldContent, sectionLines);
            changes.Add(new PatchFileChange
            {
                RelativePath = relativePath,
                OldContent = oldContent,
                NewContent = newContent
            });
        }

        if (changes.Count == 0)
        {
            throw new InvalidOperationException("Patch package parsing failed: no file changes found.");
        }

        return changes;
    }

    private static string ApplyDiffSection(string originalContent, IReadOnlyList<string> sectionLines)
    {
        var originalLines = originalContent.ReplaceLineEndings("\n").Split('\n');
        var outputLines = new List<string>();
        var originalIndex = 0;
        var index = 0;

        while (index < sectionLines.Count)
        {
            var line = sectionLines[index];

            if (!line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                line,
                @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@");

            if (!match.Success)
            {
                throw new InvalidOperationException("Patch package parsing failed: invalid hunk header.");
            }

            var oldStart = int.Parse(match.Groups["oldStart"].Value);
            var targetIndex = Math.Max(0, oldStart - 1);

            while (originalIndex < targetIndex && originalIndex < originalLines.Length)
            {
                outputLines.Add(originalLines[originalIndex]);
                originalIndex++;
            }

            index++;

            while (index < sectionLines.Count && !sectionLines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var hunkLine = sectionLines[index];

                if (hunkLine == "\\ No newline at end of file")
                {
                    index++;
                    continue;
                }

                if (hunkLine.StartsWith(" ", StringComparison.Ordinal))
                {
                    if (originalIndex >= originalLines.Length)
                    {
                        throw new InvalidOperationException("Patch package parsing failed: context line exceeds original content.");
                    }

                    outputLines.Add(originalLines[originalIndex]);
                    originalIndex++;
                }
                else if (hunkLine.StartsWith("-", StringComparison.Ordinal))
                {
                    if (originalIndex >= originalLines.Length)
                    {
                        throw new InvalidOperationException("Patch package parsing failed: removal line exceeds original content.");
                    }

                    originalIndex++;
                }
                else if (hunkLine.StartsWith("+", StringComparison.Ordinal))
                {
                    outputLines.Add(hunkLine[1..]);
                }
                else
                {
                    throw new InvalidOperationException("Patch package parsing failed: unexpected hunk line.");
                }

                index++;
            }
        }

        while (originalIndex < originalLines.Length)
        {
            outputLines.Add(originalLines[originalIndex]);
            originalIndex++;
        }

        return string.Join(Environment.NewLine, outputLines);
    }

    private static string StripDiffPathPrefix(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            return trimmed[2..];
        }

        return trimmed;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }

    private static PatchPackage Clone(PatchPackage package)
    {
        return new PatchPackage
        {
            Id = package.Id,
            CreatedAt = package.CreatedAt,
            UpdatedAt = package.UpdatedAt,
            ProjectPath = package.ProjectPath,
            UserRequest = package.UserRequest,
            Model = package.Model,
            PatchText = package.PatchText,
            Status = package.Status,
            StatusMessage = package.StatusMessage,
            ApprovalGateResults = (package.ApprovalGateResults ?? []).Select(result => new PatchApprovalGateResult
            {
                GateKey = result.GateKey,
                Message = result.Message,
                FilePath = result.FilePath,
                Passed = result.Passed,
                Blocking = result.Blocking,
                Warning = result.Warning
            }).ToArray(),
            BackupFolder = package.BackupFolder,
            AppliedAt = package.AppliedAt,
            RolledBack = package.RolledBack,
            RolledBackAt = package.RolledBackAt,
            RollbackResult = package.RollbackResult,
            RollbackError = package.RollbackError,
            FileChanges = package.FileChanges.Select(change => new PatchFileChange
            {
                RelativePath = change.RelativePath,
                OldContent = change.OldContent,
                NewContent = change.NewContent
            }).ToArray()
        };
    }

    private static PatchFileChange Clone(PatchFileChange change)
    {
        return new PatchFileChange
        {
            RelativePath = change.RelativePath,
            OldContent = change.OldContent,
            NewContent = change.NewContent
        };
    }
}
