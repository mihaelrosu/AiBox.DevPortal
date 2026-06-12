using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public static class PatchIntentService
{
    public static PatchIntent BuildIntent(LocalCoderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeMode = request.AllowedPatchScope;
        var allowedPaths = scopeMode switch
        {
            PatchScopeMode.ContextFilesOnly => request.FileContexts.Select(context => context.RelativePath).ToArray(),
            PatchScopeMode.SelectedFolders => request.AllowedPatchFolders.ToArray(),
            PatchScopeMode.AnyProjectFile => ["Any project file"],
            _ => request.FileContexts.Select(context => context.RelativePath).ToArray()
        };

        return new PatchIntent
        {
            Goal = DeriveGoal(request.Task),
            AllowedPaths = allowedPaths,
            AllowedScope = scopeMode switch
            {
                PatchScopeMode.ContextFilesOnly => "Context Files Only",
                PatchScopeMode.SelectedFolders => "Selected Folders",
                PatchScopeMode.AnyProjectFile => "Any Project File",
                _ => "Context Files Only"
            },
            ExpectedChangeType = DeriveExpectedChangeType(request.Task),
            MustNotChange =
            [
                "Patch apply logic",
                "History logic",
                "Agent profiles"
            ],
            VerificationCommand = DeriveVerificationCommand(request.Task)
        };
    }

    public static PatchIntentValidation Evaluate(PatchIntent intent, LocalCoderPatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(preview);

        var reasons = new List<string>();
        var detectedChangeType = DetectChangeType(preview);

        if (preview.ScopeAnalysis.HasOutOfScopeFiles)
        {
            reasons.Add("Patch modifies files outside the allowed scope.");
        }

        var protectedPaths = preview.FileChanges
            .Select(change => change.RelativePath)
            .Where(IsProtectedPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (protectedPaths.Length > 0)
        {
            reasons.Add($"Patch touches protected files: {string.Join(", ", protectedPaths)}.");
        }

        var status = reasons.Count > 0
            ? PatchIntentMatchStatus.DoesNotMatch
            : intent.ExpectedChangeType == PatchIntentChangeType.Unknown || intent.ExpectedChangeType == detectedChangeType
                ? PatchIntentMatchStatus.MatchesIntent
                : PatchIntentMatchStatus.PartiallyMatches;

        if (status == PatchIntentMatchStatus.PartiallyMatches)
        {
            reasons.Add(intent.ExpectedChangeType == PatchIntentChangeType.Unknown
                ? "Expected change type could not be derived confidently from the task."
                : $"Expected {intent.ExpectedChangeType} but detected {detectedChangeType}.");
        }

        return new PatchIntentValidation
        {
            Status = status,
            DetectedChangeType = detectedChangeType.ToString(),
            Reasons = reasons
        };
    }

    public static PatchIntentValidation Evaluate(PatchIntent intent, PatchPackage package)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(package);

        var preview = new LocalCoderPatchPreview
        {
            FileContexts = (package.ContextFilePaths ?? []).Select(path => new LocalCoderFileContext { RelativePath = path }).ToArray(),
            FileChanges = package.FileChanges ?? [],
            AllowedPatchScope = package.AllowedPatchScope ?? PatchScopeMode.AnyProjectFile,
            AllowedPatchFolders = package.AllowedPatchFolders ?? [],
            ScopeAnalysis = PatchScopeGuard.Analyze(
                package.AllowedPatchScope,
                package.ContextFilePaths ?? [],
                package.AllowedPatchFolders ?? [],
                (package.FileChanges ?? []).Select(change => change.RelativePath).ToArray())
        };

        return Evaluate(intent, preview);
    }

    public static string BuildPromptText(PatchIntent intent)
    {
        return $"""
        Goal:
        {intent.Goal}

        Allowed files/scope:
        {string.Join(Environment.NewLine, intent.AllowedPaths.Select(path => $"- {path}"))}

        Expected change type:
        {intent.ExpectedChangeType}

        Must not change:
        {string.Join(Environment.NewLine, intent.MustNotChange.Select(item => $"- {item}"))}

        Verification:
        {intent.VerificationCommand}
        """;
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

    private static PatchIntentChangeType DetectChangeType(LocalCoderPatchPreview preview)
    {
        if (preview.PatchText.Contains("rename from ", StringComparison.OrdinalIgnoreCase) ||
            preview.PatchText.Contains("rename to ", StringComparison.OrdinalIgnoreCase))
        {
            return PatchIntentChangeType.Move;
        }

        if (preview.PatchText.Contains("new file mode", StringComparison.OrdinalIgnoreCase))
        {
            return PatchIntentChangeType.Add;
        }

        if (preview.PatchText.Contains("deleted file mode", StringComparison.OrdinalIgnoreCase))
        {
            return PatchIntentChangeType.Remove;
        }

        if (preview.FileChanges.Count > 1)
        {
            return PatchIntentChangeType.Refactor;
        }

        return PatchIntentChangeType.Update;
    }

    private static bool IsProtectedPath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/');
        return normalized.Contains("PatchApply", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("PatchPackage", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("PatchApprovalGate", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("History", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("AgentActionProfiles", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("AgentModeRunner", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("AgentModeProfile", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("AgentProfile", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Program.cs", StringComparison.OrdinalIgnoreCase);
    }
}
