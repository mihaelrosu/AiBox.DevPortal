using System.Text.RegularExpressions;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public static class PatchIntentService
{
    public static PatchIntent BuildIntent(LocalCoderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeMode = request.AllowedPatchScope;
        var contextFiles = request.FileContexts.Select(context => context.RelativePath).ToArray();
        var createdFiles = ExtractRequestedCreateFiles(request.Task);
        var allowedCreateFolders = createdFiles.Count > 0
            ? DeriveCreateFolders(createdFiles)
            : request.AllowedCreateFolders.Count > 0
                ? NormalizeFolders(request.AllowedCreateFolders).ToArray()
                : InferCreateFolders(contextFiles);
        var allowedFiles = contextFiles
            .Concat(createdFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowedPaths = scopeMode switch
        {
            PatchScopeMode.ContextFilesOnly => allowedFiles,
            PatchScopeMode.SelectedFolders => request.AllowedPatchFolders.ToArray(),
            PatchScopeMode.AnyProjectFile => ["Any project file"],
            _ => allowedFiles
        };

        return new PatchIntent
        {
            Goal = DeriveGoal(request.Task),
            AllowedFiles = allowedFiles,
            AllowedPaths = allowedPaths,
            TargetCreatedFiles = createdFiles,
            AllowedCreateFolders = allowedCreateFolders,
            ProtectedPaths = [],
            AllowedScope = scopeMode switch
            {
                PatchScopeMode.ContextFilesOnly => "Context Files Only",
                PatchScopeMode.SelectedFolders => "Selected Folders",
                PatchScopeMode.AnyProjectFile => "Any Project File",
                _ => "Context Files Only"
            },
            PrimaryIntent = DerivePrimaryIntent(request.Task),
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
        var requestedFiles = NormalizePaths(intent.AllowedFiles.Count > 0 ? intent.AllowedFiles : intent.AllowedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var modifiedFiles = NormalizePaths(preview.FileChanges.Select(change => change.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var contextFiles = NormalizePaths(preview.FileContexts.Select(context => context.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedCreateFolders = NormalizeFolders(intent.AllowedCreateFolders).ToArray();
        var scopeFilesByPath = (preview.ScopeAnalysis.Files ?? [])
            .ToDictionary(file => NormalizePath(file.RelativePath), file => file, StringComparer.OrdinalIgnoreCase);
        var scopeFiles = NormalizePaths(preview.ScopeAnalysis.Files
                .Where(file => file.Status == PatchScopeStatus.InScope)
                .Select(file => file.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explicitAllowedFiles = new HashSet<string>(requestedFiles, StringComparer.OrdinalIgnoreCase);
        var explicitProtectedFiles = new HashSet<string>(NormalizePaths(intent.ProtectedPaths), StringComparer.OrdinalIgnoreCase);

        var fileEvaluations = modifiedFiles.Select(path =>
        {
            scopeFilesByPath.TryGetValue(path, out var scopeFile);
            var isCreate = scopeFile?.IsCreate == true;
            var inContext = contextFiles.Contains(path);
            var inScope = scopeFiles.Contains(path);
            var matchesRequestedFile = IsUnderAnyRequestedPath(path, requestedFiles) || (isCreate && IsUnderAnyRequestedPath(path, allowedCreateFolders));
            var explicitlyAllowed = explicitAllowedFiles.Contains(path) || matchesRequestedFile;
            var explicitlyProtected = explicitProtectedFiles.Contains(path) || IsProtectedPath(path);
            var protectedFile = explicitlyProtected;

            return new PatchIntentFileEvaluation
            {
                RelativePath = path,
                InContext = inContext,
                InScope = inScope,
                MatchesRequestedFile = matchesRequestedFile,
                ExplicitlyAllowed = explicitlyAllowed,
                ExplicitlyProtected = explicitlyProtected,
                Protected = protectedFile
            };
        }).ToArray();

        var protectedFiles = fileEvaluations
            .Where(evaluation => evaluation.Protected)
            .Select(evaluation => evaluation.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedModifiedFiles = requestedFiles.Length == 0
            ? []
            : fileEvaluations
                .Where(evaluation => evaluation.ExplicitlyAllowed)
                .Select(evaluation => evaluation.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var unexpectedModifiedFiles = preview.AllowedPatchScope == PatchScopeMode.AnyProjectFile
            ? []
            : modifiedFiles
                .Where(path => !fileEvaluations.Any(evaluation => evaluation.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase) && evaluation.ExplicitlyAllowed))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (preview.ScopeAnalysis.HasOutOfScopeFiles)
        {
            reasons.Add("Patch modifies files outside the allowed scope.");
        }

        if (protectedFiles.Length > 0)
        {
            reasons.Add($"Patch touches protected files: {string.Join(", ", protectedFiles)}.");
        }

        var scopePassed = !preview.ScopeAnalysis.IsBlocking;
        var requestedFilesSatisfied = preview.AllowedPatchScope == PatchScopeMode.AnyProjectFile
            ? modifiedFiles.Length > 0
            : requestedFiles.Length == 0 || requestedModifiedFiles.Length > 0;
        var changeTypeMatches = intent.ExpectedChangeType == PatchIntentChangeType.Unknown || intent.ExpectedChangeType == detectedChangeType;
        var hasProtectedFiles = protectedFiles.Length > 0;

        var status = hasProtectedFiles || !scopePassed
            ? PatchIntentMatchStatus.DoesNotMatch
            : !requestedFilesSatisfied
                ? PatchIntentMatchStatus.DoesNotMatch
                : changeTypeMatches && unexpectedModifiedFiles.Length == 0
                ? PatchIntentMatchStatus.MatchesIntent
                : PatchIntentMatchStatus.PartiallyMatches;

        if (status == PatchIntentMatchStatus.PartiallyMatches && !changeTypeMatches)
        {
            reasons.Add(intent.ExpectedChangeType == PatchIntentChangeType.Unknown
                ? "Expected change type could not be derived confidently from the task."
                : $"Expected {intent.ExpectedChangeType} but detected {detectedChangeType}.");
        }

        if (status == PatchIntentMatchStatus.PartiallyMatches && requestedFiles.Length > 0 && requestedModifiedFiles.Length == 0)
        {
            reasons.Add("Modified files do not match the requested files in the intent contract.");
        }

        if (status == PatchIntentMatchStatus.PartiallyMatches && unexpectedModifiedFiles.Length > 0)
        {
            reasons.Add($"Patch modifies additional files beyond the requested intent files: {string.Join(", ", unexpectedModifiedFiles)}.");
        }

        return new PatchIntentValidation
        {
            Status = status,
            DetectedChangeType = detectedChangeType.ToString(),
            ScopeMode = preview.AllowedPatchScope.ToString(),
            RequestedFiles = requestedFiles,
            ModifiedFiles = modifiedFiles,
            ContextFiles = contextFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            ProtectedFiles = protectedFiles,
            FileEvaluations = fileEvaluations,
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
            createdFiles.Add(normalized);
        }
    }

    private static bool TryConsumeCreateHeader(string line, out string remainder)
    {
        var match = CreateTaskHeaderRegex().Match(line);
        if (!match.Success)
        {
            remainder = string.Empty;
            return false;
        }

        remainder = match.Groups["rest"].Value.Trim();
        var delimiter = match.Groups["delimiter"].Value;

        if (delimiter.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        return CreateTaskPathRegex().Matches(remainder)
            .Select(pathMatch => NormalizePath(pathMatch.Groups["path"].Value))
            .Any(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".razor", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".css", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".js", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase));
    }

    private static Regex CreateTaskHeaderRegex()
    {
        return new Regex(@"^\s*(?:[-*]\s*)?(?:create(?:\s+file)?)(?<delimiter>\s*:\s*|\s+)(?<rest>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static Regex CreateTaskPathRegex()
    {
        return new Regex(@"(?<path>(?:[\w.\-]+[\\/])*(?:[\w.\-]+\.[\w.\-]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
        return normalized.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(normalized).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
               || ContainsSecurityTerms(normalized);
    }

    private static bool ContainsSecurityTerms(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        return lower.Contains("/auth/")
               || lower.Contains("/authentication/")
               || lower.Contains("/security/")
               || lower.Contains("/identity/")
               || lower.Contains("/jwt/")
               || lower.Contains("authservice")
               || lower.Contains("authmanager")
               || lower.Contains("sign-in")
               || lower.Contains("sign-out")
               || lower.Contains("signin")
               || lower.Contains("signout")
               || lower.Contains("token")
               || lower.Contains("secret");
    }

    private static bool IsUnderAnyRequestedPath(string relativePath, IReadOnlyList<string> requestedFiles)
    {
        return requestedFiles.Any(requestedPath => IsUnderRequestedPath(relativePath, requestedPath));
    }

    private static IReadOnlyList<string> InferCreateFolders(IEnumerable<string> contextFiles)
    {
        return DeriveCreateFolders(contextFiles);
    }

    private static IReadOnlyList<string> DeriveCreateFolders(IEnumerable<string> filePaths)
    {
        return filePaths
            .Select(NormalizePath)
            .Select(path =>
            {
                var index = path.LastIndexOf('/');
                return index <= 0 ? string.Empty : $"{path[..index]}/";
            })
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUnderRequestedPath(string relativePath, string requestedPath)
    {
        var normalizedPath = NormalizePath(relativePath);
        var normalizedRequestedPath = NormalizePath(requestedPath);

        if (string.IsNullOrWhiteSpace(normalizedRequestedPath))
        {
            return false;
        }

        if (normalizedPath.Equals(normalizedRequestedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(normalizedRequestedPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> NormalizePaths(IEnumerable<string>? paths)
{
    return (paths ?? []).Select(NormalizePath)
        .Where(path => !string.IsNullOrWhiteSpace(path));
}

    private static IEnumerable<string> NormalizeFolders(IEnumerable<string> folders)
    {
        return folders.Select(folder =>
            {
                var normalized = NormalizePath(folder);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return string.Empty;
                }

                return normalized.EndsWith("/", StringComparison.Ordinal)
                    ? normalized
                    : $"{normalized}/";
            })
            .Where(folder => !string.IsNullOrWhiteSpace(folder));
    }

    private static string NormalizePath(string? relativePath)
    {
        return (relativePath ?? string.Empty).Replace('\\', '/').Trim();
    }

    private static PatchPrimaryIntent DerivePrimaryIntent(string task)
    {
        var value = (task ?? string.Empty).ToLowerInvariant();

        if (ContainsAnyVerb(value, ModificationVerbs))
        {
            return PatchPrimaryIntent.Modify;
        }

        if (ContainsAnyVerb(value, ReadOnlyVerbs))
        {
            return PatchPrimaryIntent.ReadOnly;
        }

        return PatchPrimaryIntent.Unknown;
    }

    private static bool ContainsAnyVerb(string value, IReadOnlyList<string> verbs)
    {
        return verbs.Any(verb => value.Contains(verb, StringComparison.Ordinal));
    }

    private static readonly IReadOnlyList<string> ReadOnlyVerbs =
    [
        "inspect",
        "analyze",
        "analyse",
        "review",
        "explain",
        "summarize",
        "summarise"
    ];

    private static readonly IReadOnlyList<string> ModificationVerbs =
    [
        "add",
        "create",
        "update",
        "replace",
        "remove",
        "delete",
        "rename",
        "move",
        "refactor"
    ];
}

