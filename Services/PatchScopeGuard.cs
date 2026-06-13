using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public static class PatchScopeGuard
{
    public static PatchScopeAnalysis Analyze(
        PatchScopeMode? mode,
        IReadOnlyList<string> contextFilePaths,
        IReadOnlyList<string> allowedFolders,
        IReadOnlyList<string> changedPaths)
    {
        var effectiveMode = mode ?? PatchScopeMode.AnyProjectFile;
        var normalizedContext = NormalizePaths(contextFilePaths);
        var normalizedFolders = NormalizeFolders(allowedFolders);
        var results = new List<PatchScopeFileResult>();

        foreach (var changedPath in NormalizePaths(changedPaths))
        {
            var result = AnalyzeFile(effectiveMode, changedPath, normalizedContext, normalizedFolders, isCreate: false);
            results.Add(result);
        }

        var analysis = new PatchScopeAnalysis
        {
            Mode = effectiveMode,
            AllowedFolders = normalizedFolders,
            Files = results,
            IsBlocking = effectiveMode switch
            {
                PatchScopeMode.AnyProjectFile => false,
                PatchScopeMode.ContextFilesOnly => results.Any(file => file.Status == PatchScopeStatus.OutOfScope),
                PatchScopeMode.SelectedFolders => normalizedFolders.Count == 0 || results.Any(file => file.Status == PatchScopeStatus.OutOfScope),
                _ => false
            }
        };

        analysis.WarningMessage = effectiveMode switch
        {
            PatchScopeMode.AnyProjectFile => "Patch may modify any file in the project.",
            PatchScopeMode.SelectedFolders when normalizedFolders.Count == 0 => "Add at least one allowed folder before approving or applying this patch.",
            _ when analysis.IsBlocking => BuildBlockingMessage(analysis),
            _ => string.Empty
        };

        return analysis;
    }

    public static PatchScopeAnalysis Analyze(
        PatchScopeMode? mode,
        IReadOnlyList<string> contextFilePaths,
        IReadOnlyList<string> allowedFolders,
        IReadOnlyList<PatchFileChange> fileChanges)
    {
        var effectiveMode = mode ?? PatchScopeMode.AnyProjectFile;
        var normalizedContext = NormalizePaths(contextFilePaths);
        var normalizedFolders = NormalizeFolders(allowedFolders);
        var results = new List<PatchScopeFileResult>();

        foreach (var change in fileChanges ?? [])
        {
            var changedPath = NormalizePath(change.RelativePath);
            var isCreate = string.IsNullOrWhiteSpace(change.OldContent) && !string.IsNullOrWhiteSpace(change.NewContent);
            var result = AnalyzeFile(effectiveMode, changedPath, normalizedContext, normalizedFolders, isCreate);
            results.Add(result);
        }

        var analysis = new PatchScopeAnalysis
        {
            Mode = effectiveMode,
            AllowedFolders = normalizedFolders,
            Files = results,
            IsBlocking = effectiveMode switch
            {
                PatchScopeMode.AnyProjectFile => false,
                PatchScopeMode.ContextFilesOnly => results.Any(file => file.Status == PatchScopeStatus.OutOfScope),
                PatchScopeMode.SelectedFolders => normalizedFolders.Count == 0 || results.Any(file => file.Status == PatchScopeStatus.OutOfScope),
                _ => false
            }
        };

        analysis.WarningMessage = effectiveMode switch
        {
            PatchScopeMode.AnyProjectFile => "Patch may modify any file in the project.",
            PatchScopeMode.SelectedFolders when normalizedFolders.Count == 0 => "Add at least one allowed folder before approving or applying this patch.",
            _ when analysis.IsBlocking => BuildBlockingMessage(analysis),
            _ => string.Empty
        };

        return analysis;
    }

    public static PatchScopeAnalysis Analyze(PatchPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Analyze(
            package.AllowedPatchScope,
            package.ContextFilePaths ?? [],
            package.AllowedPatchFolders ?? [],
            package.FileChanges);
    }

    public static PatchScopeAnalysis Analyze(LocalCoderPatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Analyze(
            preview.AllowedPatchScope,
            preview.FileContexts.Select(context => context.RelativePath).ToArray(),
            preview.AllowedPatchFolders,
            preview.FileChanges);
    }

    public static void ThrowIfBlocking(PatchPackage package)
    {
        var analysis = Analyze(package);
        if (analysis.IsBlocking)
        {
            throw new InvalidOperationException(BuildBlockingMessage(analysis));
        }
    }

    public static void ThrowIfBlocking(LocalCoderPatchPreview preview)
    {
        var analysis = Analyze(preview);
        if (analysis.IsBlocking)
        {
            throw new InvalidOperationException(BuildBlockingMessage(analysis));
        }
    }

    public static string BuildBlockingMessage(PatchScopeAnalysis analysis)
    {
        var outOfScopeFiles = analysis.Files
            .Where(file => file.Status == PatchScopeStatus.OutOfScope)
            .Select(file => file.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (analysis.Mode == PatchScopeMode.SelectedFolders && analysis.AllowedFolders.Count == 0)
        {
            return "Selected Folders scope requires at least one allowed folder before approving or applying the patch.";
        }

        if (outOfScopeFiles.Length == 0)
        {
            return "Patch scope validation failed.";
        }

        return analysis.Mode switch
        {
            PatchScopeMode.ContextFilesOnly =>
                $"Patch modifies files outside the selected context: {string.Join(", ", outOfScopeFiles)}.",
            PatchScopeMode.SelectedFolders =>
                $"Patch modifies files outside the allowed folders: {string.Join(", ", outOfScopeFiles)}.",
            _ => "Patch scope validation failed."
        };
    }

    private static PatchScopeFileResult AnalyzeFile(
        PatchScopeMode mode,
        string changedPath,
        IReadOnlyList<string> contextPaths,
        IReadOnlyList<string> allowedFolders,
        bool isCreate)
    {
        var representativePath = string.Empty;
        var inScope = mode switch
        {
            PatchScopeMode.ContextFilesOnly => AnalyzeContextFileScope(changedPath, contextPaths, isCreate, out representativePath),
            PatchScopeMode.SelectedFolders => allowedFolders.Any(folder => changedPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)),
            PatchScopeMode.AnyProjectFile => true,
            _ => false
        };

        return new PatchScopeFileResult
        {
            RelativePath = changedPath,
            Status = inScope ? PatchScopeStatus.InScope : PatchScopeStatus.OutOfScope,
            IsCreate = isCreate,
            ContextRepresentativePath = representativePath,
            Reason = inScope
                ? string.Empty
                : mode switch
                {
                    PatchScopeMode.ContextFilesOnly => isCreate
                        ? "Parent folder is not represented in the selected context."
                        : "Not in selected context.",
                    PatchScopeMode.SelectedFolders => "Not under an allowed folder.",
                    _ => string.Empty
                }
        };
    }

    private static bool AnalyzeContextFileScope(
        string changedPath,
        IReadOnlyList<string> contextPaths,
        bool isCreate,
        out string representativePath)
    {
        representativePath = string.Empty;

        if (contextPaths.Contains(changedPath, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!isCreate)
        {
            return false;
        }

        var parentDirectory = GetParentDirectory(changedPath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return false;
        }

        representativePath = contextPaths.FirstOrDefault(contextPath =>
            string.Equals(GetParentDirectory(contextPath), parentDirectory, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        return !string.IsNullOrWhiteSpace(representativePath);
    }

    private static IReadOnlyList<string> NormalizePaths(IReadOnlyList<string> paths)
    {
        return paths
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeFolders(IReadOnlyList<string> folders)
    {
        return folders
            .Select(NormalizeFolder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string NormalizeFolder(string folder)
    {
        var normalized = NormalizePath(folder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.EndsWith("/", StringComparison.Ordinal)
            ? normalized
            : $"{normalized}/";
    }

    private static string GetParentDirectory(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }
}
