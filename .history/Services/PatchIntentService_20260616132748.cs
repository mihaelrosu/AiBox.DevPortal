using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
namespace AiBox.DevPortal.Services
{
    public class PatchIntentService
    {
        public static LocalCoderPatchPreview EvaluatePatchIntent(PatchIntent intent, LocalCoderPatchPackage package)
        {
        ArgumentNullException.ThrowIfNull(intent);
            ArgumentNullException.ThrowIfNull(package);

        var preview = new LocalCoderPatchPreview
        {
            FileContexts = (package.ContextFilePaths ?? []).Select(path => new LocalCoderFileContext { RelativePath = path }).ToArray(),
            FileChanges = package.FileChanges ?? [],
            AllowedPatchScope = package.AllowedPatchScope ?? PatchScopeMode.AnyProjectFile,
            AllowedPatchFolders = package.AllowedPatchFolders ?? [],
            AllowedCreateFolders = package.AllowedCreateFolders ?? [],
            ScopeAnalysis = PatchScopeGuard.Analyze(
                package.AllowedPatchScope,
                package.ContextFilePaths ?? [],
                package.AllowedPatchFolders ?? [],
                package.AllowedCreateFolders ?? [],
                (package.FileChanges ?? []).Select(change => change.RelativePath).ToArray())
        };

        return Evaluate(intent, preview);
    }

    public static string BuildPromptText(PatchIntent intent)
    {
        var allowedFiles = intent.AllowedFiles.Count > 0 ? intent.AllowedFiles : intent.AllowedPaths;
        return $"""
        Goal:
        {intent.Goal}

        Allowed files:
        {string.Join(Environment.NewLine, allowedFiles.Select(path => $"- {path}"))}

        {(intent.TargetCreatedFiles.Count > 0
            ? $"""
        Target created file(s):
        {string.Join(Environment.NewLine, intent.TargetCreatedFiles.Select(path => $"- {path}"))}
        """
            : string.Empty)}

        {(intent.AllowedCreateFolders.Count > 0
            ? $"""
        Allowed create folders:
        {string.Join(Environment.NewLine, intent.AllowedCreateFolders.Select(path => $"- {path}"))}
        """
            : string.Empty)}

        Expected change type:
        {intent.ExpectedChangeType}

        Must not change:
        {string.Join(Environment.NewLine, intent.MustNotChange.Select(item => $"- {item}"))}

        Verification:
        {intent.VerificationCommand}
        """;
    }

    public static IReadOnlyList<string> ExtractRequestedCreateFiles(string task)
    {
        var value = task ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var createdFiles = new List<string>();
        var inCreateSection = false;

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                inCreateSection = false;
                continue;
            }

            if (TryConsumeCreateHeader(trimmed, out var remainder))
            {
                inCreateSection = true;
                AddCreatePaths(createdFiles, remainder);
                continue;
            }

            if (!inCreateSection)
            {
                continue;
            }

            AddCreatePaths(createdFiles, trimmed);
        }

        return createdFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool HasExplicitCreateRequest(string task)
    {
        return ExtractRequestedCreateFiles(task).Count > 0;
    }

    private static string DeriveGoal(string task)
    {
        var trimmed = (task ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Implement the requested patch safely.";
        }

        var sentenceEnd = trimmed.IndexOfAny(['.', '!', '?']);
        return sentenceEnd > 0 ? trimmed[..sentenceEnd].Trim() : trimmed;
    }

    private static PatchIntentChangeType DeriveExpectedChangeType(string task)
    {
        var value = (task ?? string.Empty).ToLowerInvariant();
        if (value.Contains("rename") || value.Contains("move"))
        {
            return PatchIntentChangeType.Move;
        }

        if (value.Contains("extract"))
        {
            return PatchIntentChangeType.Refactor;
        }

        if (value.Contains("refactor"))
        {
            return PatchIntentChangeType.Refactor;
        }

        if (value.Contains("remove") || value.Contains("delete"))
        {
            return PatchIntentChangeType.Remove;
        }

        if (value.Contains("add") || value.Contains("create"))
        {
            return PatchIntentChangeType.Add;
        }

        if (value.Contains("update") || value.Contains("modify") || value.Contains("fix") || value.Contains("change"))
        {
            return PatchIntentChangeType.Update;
        }

        return PatchIntentChangeType.Unknown;
    }

    private static string DeriveVerificationCommand(string task)
    {
        var value = (task ?? string.Empty).ToLowerInvariant();
        if (value.Contains("test"))
        {
            return "dotnet test";
        }

        return "dotnet build";
    }

    private static void AddCreatePaths(List<string> createdFiles, string text)
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".razor", ".md", ".json", ".css", ".js", ".ts", ".html"
        };

        foreach (Match match in CreateTaskPathRegex().Matches(text ?? string.Empty))
        {
            var normalized = NormalizePath(match.Groups["path"].Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!allowedExtensions.Contains(Path.GetExtension(normalized)))
            {
                continue;
            }

            if (!createdFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                createdFiles.Add(normalized);
            }
        }
    }

    private static void AddCreatePath(List<string> createdFiles, string path)
    {
        var normalized = NormalizePath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
                created

