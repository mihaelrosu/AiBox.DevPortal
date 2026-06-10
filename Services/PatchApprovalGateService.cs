using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PatchApprovalGateService : IPatchApprovalGateService
{
    public async Task<IReadOnlyList<PatchApprovalGateResult>> EvaluateAsync(PatchPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var results = new List<PatchApprovalGateResult>();
        var rootPath = GetValidatedProjectRoot(package.ProjectPath);

        if (package.FileChanges is null || package.FileChanges.Count == 0)
        {
            results.Add(Fail(
                "patch-has-files",
                "Patch package does not contain any file changes.",
                blocking: true));
            return results;
        }

        results.Add(Pass("patch-has-files", $"Patch contains {package.FileChanges.Count} file change(s)."));

        foreach (var change in package.FileChanges)
        {
            var relativePath = NormalizeRelativePath(change.RelativePath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                results.Add(Fail(
                    "file-path-not-empty",
                    "A file change has an empty path.",
                    blocking: true));
                continue;
            }

            results.Add(Pass(
                "file-path-not-empty",
                $"File path provided: {relativePath}.",
                relativePath));

            string targetPath;
            try
            {
                targetPath = GetValidatedTargetPath(rootPath, relativePath);
            }
            catch (Exception exception)
            {
                results.Add(Fail(
                    "target-file-inside-project-root",
                    exception.Message,
                    relativePath,
                    blocking: true));
                continue;
            }

            results.Add(Pass(
                "target-file-inside-project-root",
                $"{relativePath} resolves to {targetPath}.",
                relativePath));

            if (ContainsRestrictedSegment(relativePath))
            {
                results.Add(Fail(
                    "restricted-path",
                    "File path must not contain /bin/, /obj/, or /.git/.",
                    relativePath,
                    blocking: true));
            }
            else
            {
                results.Add(Pass(
                    "restricted-path",
                    $"{relativePath} is not inside a restricted folder.",
                    relativePath));
            }

            var currentContent = await ReadCurrentFileContentAsync(targetPath, cancellationToken);
            var originalContent = change.OldContent ?? string.Empty;
            if (!string.Equals(originalContent, currentContent, StringComparison.Ordinal))
            {
                results.Add(Fail(
                    "original-content-match",
                    "Current file content has changed since the patch preview was generated.",
                    relativePath,
                    blocking: true));
            }
            else
            {
                results.Add(Pass(
                    "original-content-match",
                    $"{relativePath} matches the current file content.",
                    relativePath));
            }

            if (IsAppSettingsFile(relativePath))
            {
                results.Add(new PatchApprovalGateResult
                {
                    GateKey = "appsettings-warning",
                    Message = "Editing appsettings*.json requires review.",
                    FilePath = relativePath,
                    Passed = true,
                    Blocking = false,
                    Warning = true
                });

                if (ContainsSensitiveConfiguration(currentContent) || ContainsSensitiveConfiguration(change.NewContent ?? string.Empty))
                {
                    results.Add(Fail(
                        "appsettings-sensitive-data",
                        "Editing appsettings*.json with passwords, tokens, secrets, api keys, or connection strings is blocked.",
                        relativePath,
                        blocking: true));
                }
            }
        }

        return results;
    }

    private static PatchApprovalGateResult Pass(string gateKey, string detail, string? filePath = null)
    {
        return new PatchApprovalGateResult
        {
            GateKey = gateKey,
            Message = detail,
            FilePath = filePath ?? string.Empty,
            Passed = true,
            Blocking = false,
            Warning = false
        };
    }

    private static PatchApprovalGateResult Fail(string gateKey, string detail, string? filePath = null, bool blocking = true)
    {
        return new PatchApprovalGateResult
        {
            GateKey = gateKey,
            Message = detail,
            FilePath = filePath ?? string.Empty,
            Passed = false,
            Blocking = blocking,
            Warning = false
        };
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return (relativePath ?? string.Empty).Replace('\\', '/').Trim();
    }

    private static bool ContainsRestrictedSegment(string relativePath)
    {
        var segments = NormalizeRelativePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAppSettingsFile(string relativePath)
    {
        var fileName = Path.GetFileName(NormalizeRelativePath(relativePath));
        return fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSensitiveConfiguration(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var terms = new[]
        {
            "password",
            "passwords",
            "token",
            "tokens",
            "secret",
            "secrets",
            "api key",
            "apikey",
            "api_key",
            "connection string",
            "connectionstrings",
            "connection strings"
        };

        return terms.Any(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadCurrentFileContentAsync(string targetPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(targetPath, cancellationToken);
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
        var normalized = NormalizeRelativePath(relativePath);
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
