using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Services;

public sealed class CoderConsoleService(
    HttpClient httpClient,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IPatchEditOperationService patchEditOperationService,
    ILocalCoderContextService localCoderContextService,
    PatchVerificationService patchVerificationService,
    SelectedContextValidator selectedContextValidator,
    PatchPreviewRepairService patchPreviewRepairService,
    AgentInstructionService? agentInstructionService = null) : ICoderConsoleService
{
    private const long MaxProjectFileSizeBytes = 200 * 1024;
    private const int MaxPromptFileCharacters = 12_000;
    private const string CreatedFileBackupSuffix = ".aibox-created";

    private static readonly HashSet<string> AllowedProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".json", ".yml", ".yaml", ".css", ".html", ".js", ".md", ".csproj", ".sln", ".props", ".targets"
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        "Data",
        "node_modules"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private long patchPreviewAttempts;
    private long patchPreviewSuccessfulPreviews;
    private long patchPreviewFailedPreviews;
    private long patchPreviewRepairedPreviews;

    public Task<IReadOnlyList<string>> GetWorkspaceRootsAsync()
    {
        var allowedRoots = GetAllowedWorkspaceRoots();
        var projectRoots = allowedRoots
            .SelectMany(GetProjectRoots)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(projectRoots.Length > 0 ? projectRoots : allowedRoots);
    }

    public async Task<IReadOnlyList<string>> GetOllamaModelsAsync()
    {
        var response = await httpClient.GetFromJsonAsync<OllamaTagsResponse>("/api/tags");
        return response?.Models.Select(x => x.Name).OrderBy(x => x).ToArray() ?? [];
    }

    public Task<IReadOnlyList<ProjectFileItem>> GetProjectFilesAsync(string projectPath)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);
        var files = new List<ProjectFileItem>();

        foreach (var filePath in EnumerateProjectFiles(rootPath))
        {
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Length > MaxProjectFileSizeBytes)
            {
                continue;
            }

            files.Add(new ProjectFileItem
            {
                RelativePath = Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/'),
                SizeBytes = fileInfo.Length
            });
        }

        return Task.FromResult<IReadOnlyList<ProjectFileItem>>(
            files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<IReadOnlyList<LocalCoderFileContext>> ReadFileContextsAsync(string projectPath, IReadOnlyList<string> relativePaths)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);
        return await localCoderContextService.LoadAsync(rootPath, relativePaths);
    }

    public async Task<ConsoleLocalCoderTask> CreatePlanAsync(
        LocalCoderRequest request,
        AgentModeProfile? profile = null,
        AgentModelRoute? route = null)
    {
        ValidateProjectPath(request.ProjectPath);

        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new InvalidOperationException("Task is required.");
        }

        request.FileContexts = (request.FileContexts ?? [])
            .ToList();

        var fileContextText = BuildFileContextText(request.FileContexts);
        var instructionService = GetAgentInstructionService();
        var agentInstructionContext = await instructionService.BuildContextAsync(request.ProjectPath, request.FileContexts.Select(fileContext => fileContext.RelativePath));
        var agentInstructionsText = BuildAgentInstructionsText(agentInstructionContext);

        var prompt = BuildCreatePlanPrompt(request, fileContextText, profile, agentInstructionsText);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? configuration["AiBox:LocalCoder:DefaultModel"] ?? "qwen2.5-coder:7b"
            : request.Model;
        var resultText = await GenerateTextAsync(model, prompt, route);

        var task = new ConsoleLocalCoderTask
        {
            ProjectPath = request.ProjectPath,
            Model = model,
            Task = request.Task,
            Plan = resultText
        };

        await SaveTaskAsync(task);
        return task;
    }

    public async Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(
        LocalCoderRequest request,
        AgentModeProfile? profile = null,
        PatchPreviewRepairContext? repairContext = null,
        AgentModelRoute? route = null)
    {
        ValidateProjectPath(request.ProjectPath);

        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new InvalidOperationException("Task is required.");
        }

        if (IsPlanningOnlyTask(request.Task))
        {
            throw new InvalidOperationException("This looks like a planning task. Use Create Plan instead of Patch Preview.");
        }

        request.FileContexts = (request.FileContexts ?? [])
            .ToList();

        var intent = PatchIntentService.BuildIntent(request);
        RecordPatchPreviewAttempt();
        try
        {
            selectedContextValidator.ValidateForPatchPreview(request.FileContexts, intent);
        }
        catch (PatchPreviewValidationException)
        {
            RecordPatchPreviewFailure();
            throw;
        }
        var intentText = PatchIntentService.BuildPromptText(intent);
        var xmlDocumentationMode = IsXmlDocumentationRequest(request.Task);
        var deterministicTargetResolution = ResolveDeterministicTargetResolution(
            request.Task,
            request.FileContexts,
            out var deterministicTargetError);

        if (deterministicTargetError is not null)
        {
            RecordPatchPreviewFailure();
            var validationException = new PatchPreviewValidationException(
                $"Patch preview validation failed:{Environment.NewLine}- {deterministicTargetError}",
                [deterministicTargetError],
                string.Empty,
                string.Empty,
                string.Empty);
            validationException.PromptTargetResolution = deterministicTargetResolution;
            throw validationException;
        }

        if (TryParseExactRemovalTask(request.Task, out var removalText))
        {
            var deterministicPatchText = BuildExactReplacementPatch(request.FileContexts, removalText, string.Empty);
            var deterministicValidation = ValidatePatchPreview(deterministicPatchText, request.FileContexts, request.Task, intent);
            var deterministicScopePaths = FindExactReplacementTargetPaths(request.FileContexts, removalText);

            if (!deterministicValidation.IsValid)
            {
                throw new InvalidOperationException($"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", deterministicValidation.Errors)}");
            }

            var preview = new LocalCoderPatchPreview
            {
                ProjectPath = request.ProjectPath,
                Model = "deterministic-exact-removal",
                Task = request.Task,
                FileContexts = request.FileContexts.ToArray(),
                AllowedPatchScope = request.AllowedPatchScope,
                AllowedPatchFolders = request.AllowedPatchFolders.ToArray(),
                AllowedCreateFolders = intent.AllowedCreateFolders.ToArray(),
                ScopeAnalysis = PatchScopeGuard.Analyze(
                    request.AllowedPatchScope,
                    request.FileContexts.Select(context => context.RelativePath).ToArray(),
                    request.AllowedPatchFolders,
                    intent.AllowedCreateFolders,
                    deterministicScopePaths),
                Intent = intent,
                PatchText = deterministicPatchText
            };
            preview.ContextCoverage = PatchContextCoverageAnalyzer.Analyze(preview.PatchText, preview.FileContexts);
            preview.IntentValidation = PatchIntentService.Evaluate(intent, preview);
            RecordPatchPreviewSuccess();
            return preview;
        }

        if (TryParseExactReplacementTask(request.Task, out var oldText, out var newText))
        {
            var deterministicPatchText = BuildExactReplacementPatch(request.FileContexts, oldText, newText);
            var deterministicValidation = ValidatePatchPreview(deterministicPatchText, request.FileContexts, request.Task, intent);
            var deterministicScopePaths = FindExactReplacementTargetPaths(request.FileContexts, oldText);

            if (!deterministicValidation.IsValid)
            {
                throw new InvalidOperationException($"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", deterministicValidation.Errors)}");
            }

            var preview = new LocalCoderPatchPreview
            {
                ProjectPath = request.ProjectPath,
                Model = "deterministic-exact-replacement",
                Task = request.Task,
                FileContexts = request.FileContexts.ToArray(),
                AllowedPatchScope = request.AllowedPatchScope,
                AllowedPatchFolders = request.AllowedPatchFolders.ToArray(),
                AllowedCreateFolders = intent.AllowedCreateFolders.ToArray(),
                ScopeAnalysis = PatchScopeGuard.Analyze(
                    request.AllowedPatchScope,
                    request.FileContexts.Select(context => context.RelativePath).ToArray(),
                    request.AllowedPatchFolders,
                    intent.AllowedCreateFolders,
                    deterministicScopePaths),
                Intent = intent,
                PatchText = deterministicPatchText
            };
            preview.ContextCoverage = PatchContextCoverageAnalyzer.Analyze(preview.PatchText, preview.FileContexts);
            preview.IntentValidation = PatchIntentService.Evaluate(intent, preview);
            RecordPatchPreviewSuccess();
            return preview;
        }

        var fileContextText = BuildPatchPreviewFileContextText(request.FileContexts);
        var instructionService = GetAgentInstructionService();
        var agentInstructionContext = await instructionService.BuildContextAsync(request.ProjectPath, request.FileContexts.Select(fileContext => fileContext.RelativePath));
        var agentInstructionsText = BuildAgentInstructionsText(agentInstructionContext);
        var promptAudit = BuildPatchPromptAudit(request.FileContexts, fileContextText);
        var selectedFilePathsText = BuildSelectedFilePathsText(request.FileContexts);
        var scopeText = BuildPatchScopeText(
            request.AllowedPatchScope,
            request.AllowedPatchFolders,
            intent.AllowedCreateFolders,
            request.FileContexts.Count == 0 && intent.TargetCreatedFiles.Count > 0);

        var prompt = BuildGeneratePatchPreviewPrompt(
            request,
            selectedFilePathsText,
            fileContextText,
            scopeText,
            intentText,
            intent.TargetCreatedFiles,
            xmlDocumentationMode,
            deterministicTargetResolution,
            repairContext,
            profile,
            agentInstructionsText);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? configuration["AiBox:LocalCoder:DefaultModel"] ?? "qwen2.5-coder:7b"
            : request.Model;
        var rawResponse = await GenerateTextAsync(model, prompt, route);
        var normalizedResponse = NormalizePatchPreviewModelResponse(rawResponse);
        var finalRawResponse = rawResponse;
        var finalNormalizedResponse = normalizedResponse;
        PatchPreviewRepairSummary? repairSummary = null;
        PatchPreviewValidationException? originalValidationException = null;

        PatchEditOperationResult editResult;
        try
        {
            if (xmlDocumentationMode)
            {
                ValidateXmlDocumentationOperationModes(finalNormalizedResponse);
            }

            editResult = await patchEditOperationService.BuildAsync(
                request.ProjectPath,
                request.FileContexts,
                finalNormalizedResponse,
                intent);
        }
        catch (PatchPreviewValidationException exception)
        {
            exception.PromptAudit = promptAudit;
            exception.PromptTargetResolution = deterministicTargetResolution;
            originalValidationException = exception;
            if (!TryGetPatchPreviewRepairPlan(exception, out var repairPlan))
            {
                RecordPatchPreviewFailure();
                throw;
            }

            var repairOutcome = await TryRepairPatchPreviewAsync(
                request,
                profile,
                intent,
                intentText,
                selectedFilePathsText,
                fileContextText,
                scopeText,
                xmlDocumentationMode,
                deterministicTargetResolution,
                repairPlan,
                exception,
                agentInstructionsText);

            if (repairOutcome is null)
            {
                exception.RepairSummary = new PatchPreviewRepairSummary
                {
                    OriginalOperation = repairPlan.OriginalOperation,
                    RepairAttempt = repairPlan.RepairAttempt,
                    RepairResult = "Failed",
                    ValidationError = exception.Message
                };
                RecordPatchPreviewFailure();
                throw;
            }

            if (!repairOutcome.Success || repairOutcome.EditResult is null)
            {
                var validationException = repairOutcome.ValidationException ?? originalValidationException ?? exception;
                validationException.PromptAudit = promptAudit;
                validationException.PromptTargetResolution = deterministicTargetResolution;
                validationException.RepairSummary = repairOutcome.RepairSummary;
                RecordPatchPreviewFailure();
                throw validationException;
            }

            editResult = repairOutcome.EditResult;
            repairSummary = repairOutcome.RepairSummary;
            finalRawResponse = repairOutcome.RepairRawResponse;
            finalNormalizedResponse = repairOutcome.RepairNormalizedResponse;
        }
        catch (Exception exception)
        {
            RecordPatchPreviewFailure();
            var validationException = new PatchPreviewValidationException(
                $"Patch preview validation failed:{Environment.NewLine}- {exception.Message}",
                [exception.Message],
                rawResponse,
                string.Empty,
                normalizedResponse);
            validationException.PromptAudit = promptAudit;
            validationException.PromptTargetResolution = deterministicTargetResolution;
            throw validationException;
        }

        await SavePatchDebugRawResponseAsync(finalRawResponse);

        var patchText = editResult.PatchText;
        var validation = ValidatePatchPreview(patchText, request.FileContexts, request.Task, intent, editResult.FileChanges);
        var finalValidationException = CreatePatchPreviewValidationException(validation, finalRawResponse, patchText, finalNormalizedResponse);

        if (!validation.IsValid)
        {
            if (originalValidationException is not null && repairSummary is not null)
            {
                repairSummary.RepairResult = "Failed";
                repairSummary.ValidationError = string.Join(
                    Environment.NewLine,
                    originalValidationException.ValidationErrors.Concat(validation.Errors).Distinct(StringComparer.OrdinalIgnoreCase));

                var combinedErrors = originalValidationException.ValidationErrors
                    .Concat(validation.Errors)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var combinedMessage = $"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", combinedErrors)}";
                var combinedException = new PatchPreviewValidationException(
                    combinedMessage,
                    combinedErrors,
                    finalRawResponse,
                    patchText,
                    finalNormalizedResponse,
                    finalValidationException.OperationGrammarErrors,
                    finalValidationException.Guidance,
                    finalValidationException.ReplaceDiagnostics,
                    finalValidationException.SuggestedTargetDiagnostics);
                combinedException.PromptAudit = promptAudit;
                combinedException.PromptTargetResolution = deterministicTargetResolution;
                combinedException.RepairSummary = repairSummary;
                RecordPatchPreviewFailure();
                throw combinedException;
            }

            RecordPatchPreviewFailure();
            finalValidationException.PromptAudit = promptAudit;
            finalValidationException.PromptTargetResolution = deterministicTargetResolution;
            throw finalValidationException;
        }

        var patchPreview = new LocalCoderPatchPreview
        {
            ProjectPath = request.ProjectPath,
            Model = model,
            Task = request.Task,
            FileContexts = request.FileContexts.ToArray(),
            FileChanges = editResult.FileChanges,
            AllowedPatchScope = request.AllowedPatchScope,
            AllowedPatchFolders = request.AllowedPatchFolders.ToArray(),
            AllowedCreateFolders = intent.AllowedCreateFolders.ToArray(),
            ScopeAnalysis = PatchScopeGuard.Analyze(
                request.AllowedPatchScope,
                request.FileContexts.Select(context => context.RelativePath).ToArray(),
                request.AllowedPatchFolders,
                intent.AllowedCreateFolders,
                editResult.FileChanges),
            Intent = intent,
            PromptAudit = promptAudit,
            PromptTargetResolution = deterministicTargetResolution,
            PatchText = patchText,
            RepairSummary = repairSummary
        };
        patchPreview.ContextCoverage = PatchContextCoverageAnalyzer.Analyze(patchPreview.PatchText, patchPreview.FileContexts);
        patchPreview.IntentValidation = PatchIntentService.Evaluate(intent, patchPreview);
        RecordPatchPreviewSuccess();
        if (repairSummary is not null)
        {
            RecordPatchPreviewRepaired();
        }
        return patchPreview;
    }

    private async Task<string> GenerateTextAsync(string model, string prompt, AgentModelRoute? route)
    {
        if (IsOpenAiCompatibleRoute(route))
        {
            var request = new ChatCompletionRequest
            {
                Model = model,
                Messages =
                [
                    new ChatCompletionMessage
                    {
                        Role = "user",
                        Content = prompt
                    }
                ],
                MaxTokens = 8_000,
                Stream = false
            };

            using var response = await httpClient.PostAsJsonAsync(GetRouteEndpoint(route), request);
            response.EnsureSuccessStatusCode();

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>();
            return completion?.Choices.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
        }

        var ollamaRequest = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = prompt,
            Stream = false
        };

        using var ollamaResponse = await httpClient.PostAsJsonAsync(GetOllamaEndpoint(route), ollamaRequest);
        ollamaResponse.EnsureSuccessStatusCode();

        var result = await ollamaResponse.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        return result?.Response?.Trim() ?? string.Empty;
    }

    private static bool IsOpenAiCompatibleRoute(AgentModelRoute? route)
    {
        return route is not null
               && (route.Provider.Contains("llama.cpp", StringComparison.OrdinalIgnoreCase)
                   || route.BaseUrl.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetRouteEndpoint(AgentModelRoute? route)
    {
        if (route is null || string.IsNullOrWhiteSpace(route.BaseUrl))
        {
            throw new InvalidOperationException("A model route endpoint is required.");
        }

        return route.BaseUrl;
    }

    private static string GetOllamaEndpoint(AgentModelRoute? route)
    {
        if (route is not null && route.Provider.Contains("Ollama", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(route.BaseUrl))
        {
            return route.BaseUrl.TrimEnd('/') + "/api/generate";
        }

        return "/api/generate";
    }

    public PatchPreviewMetricsSnapshot GetPatchPreviewMetrics()
    {
        return new PatchPreviewMetricsSnapshot
        {
            Attempts = Interlocked.Read(ref patchPreviewAttempts),
            SuccessfulPreviews = Interlocked.Read(ref patchPreviewSuccessfulPreviews),
            FailedPreviews = Interlocked.Read(ref patchPreviewFailedPreviews),
            RepairedPreviews = Interlocked.Read(ref patchPreviewRepairedPreviews)
        };
    }

    public async Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview)
    {
        ArgumentNullException.ThrowIfNull(patchPreview);

        var rootPath = GetValidatedProjectRoot(patchPreview.ProjectPath);
        PatchScopeGuard.ThrowIfBlocking(patchPreview);
        PatchIntentGuard.ThrowIfBlocking(patchPreview);
        var patchText = NormalizePatchPreviewText(patchPreview.PatchText);

        if (patchText.Contains("PATCH_NOT_POSSIBLE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PATCH_NOT_POSSIBLE cannot be applied.");
        }

        if (!patchText.StartsWith("diff --git", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Patch apply requires PatchText that starts with diff --git.");
        }

        var validation = ValidatePatchPreview(patchText, patchPreview.FileContexts, patchPreview.Task, patchPreview.Intent, patchPreview.FileChanges);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Patch apply validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", validation.Errors)}");
        }

        var changedFiles = ExtractChangedFilePathsFromDiffHeaders(patchText.ReplaceLineEndings("\n").Split('\n'))
            .SelectMany(filePaths => new[] { filePaths.OldPath, filePaths.NewPath })
            .Select(NormalizePreviewPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !IsDevNullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (changedFiles.Length == 0)
        {
            throw new InvalidOperationException("Patch does not contain any changed file paths.");
        }

        foreach (var changedFile in changedFiles)
        {
            if (Path.IsPathRooted(changedFile) ||
                changedFile.StartsWith("/", StringComparison.Ordinal) ||
                changedFile.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Patch apply references an invalid file path: {changedFile}");
            }
        }

        var tempPatchPath = Path.Combine(Path.GetTempPath(), $"aibox-local-coder-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(tempPatchPath, patchText + Environment.NewLine);

        try
        {
            var backupRoot = CreatePatchBackupDirectory(rootPath);
            var backupFiles = new List<string>();
            var originalFileContents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var changedFile in changedFiles)
            {
                var normalizedRelativePath = ValidateAndNormalizePatchTargetPath(rootPath, changedFile);
                var sourcePath = Path.Combine(rootPath, normalizedRelativePath);

                if (File.Exists(sourcePath))
                {
                    var backupPath = Path.Combine(backupRoot, normalizedRelativePath);
                    originalFileContents[normalizedRelativePath] = await File.ReadAllBytesAsync(sourcePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(sourcePath, backupPath, overwrite: false);
                    backupFiles.Add(Path.GetRelativePath(rootPath, backupPath).Replace('\\', '/'));
                }
                else
                {
                    var backupPath = Path.Combine(backupRoot, normalizedRelativePath + CreatedFileBackupSuffix);
                    originalFileContents[normalizedRelativePath] = [];
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    await File.WriteAllTextAsync(backupPath, string.Empty);
                    backupFiles.Add(Path.GetRelativePath(rootPath, backupPath).Replace('\\', '/'));
                }
            }

            var applyResult = await RunFixedCommandAsync(
                rootPath,
                "git",
                ["apply", "--whitespace=fix", tempPatchPath],
                $"git apply --whitespace=fix {tempPatchPath}");

            if (applyResult.ExitCode != 0)
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = $"Patch apply failed after backups were created: {GetCommandFailureMessage(applyResult)}",
                    GitApplyResult = applyResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = []
                };
            }

            var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");

            if (diffStatResult.ExitCode != 0)
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = $"Patch apply failed: could not verify repository changes. {GetCommandFailureMessage(diffStatResult)}",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult]
                };
            }

            if (string.IsNullOrWhiteSpace(diffStatResult.Output))
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = "Patch apply failed: no repository changes detected after git apply.",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult]
                };
            }

            var changedTargetProven = false;
            foreach (var changedFile in changedFiles)
            {
                var normalizedRelativePath = ValidateAndNormalizePatchTargetPath(rootPath, changedFile);
                var currentContent = await File.ReadAllBytesAsync(Path.Combine(rootPath, normalizedRelativePath));

                if (!currentContent.AsSpan().SequenceEqual(originalFileContents[normalizedRelativePath]))
                {
                    changedTargetProven = true;
                    break;
                }
            }

            if (!changedTargetProven)
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = "Patch apply failed: no changed file updates detected after git apply.",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult]
                };
            }

            var changedFilesDiffArguments = new List<string> { "diff", "--" };
            changedFilesDiffArguments.AddRange(changedFiles);
            var changedFilesDiffResult = await RunFixedCommandAsync(
                rootPath,
                "git",
                changedFilesDiffArguments,
                $"git diff -- {string.Join(' ', changedFiles)}");

            if (changedFilesDiffResult.ExitCode != 0 || string.IsNullOrWhiteSpace(changedFilesDiffResult.Output))
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = changedFilesDiffResult.ExitCode != 0
                        ? $"Patch apply failed: could not verify changed file diff. {GetCommandFailureMessage(changedFilesDiffResult)}"
                        : "Patch apply failed: no changed file diff detected after git apply.",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFilesDiffResult = changedFilesDiffResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult, changedFilesDiffResult]
                };
            }

            var verification = await patchVerificationService.VerifyAsync(rootPath);
            var verificationResults = verification.Commands
                .Select(command => new CommandRunResult
                {
                    Command = command.Command,
                    ExitCode = command.ExitCode,
                    Output = command.Output,
                    Error = command.Error,
                    Started = command.StartedAt,
                    Finished = command.FinishedAt
                })
                .ToArray();

            return new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = verification.Passed,
                Message = verification.Passed
                    ? $"Patch applied successfully.{Environment.NewLine}{Environment.NewLine}{verification.Summary}"
                    : $"Patch applied successfully.{Environment.NewLine}{Environment.NewLine}{verification.Summary}",
                GitApplyResult = applyResult,
                GitDiffStatResult = diffStatResult,
                ChangedFilesDiffResult = changedFilesDiffResult,
                ChangedFiles = changedFiles,
                BackupFiles = backupFiles,
                VerificationResults = [diffStatResult, changedFilesDiffResult, .. verificationResults]
            };
        }
        finally
        {
            File.Delete(tempPatchPath);
        }
    }

    public async Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(
        LocalCoderPatchApplyResult applyResult,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(applyResult);

        var rootPath = GetValidatedProjectRoot(projectPath);

        if (applyResult.BackupFiles.Count == 0)
        {
            throw new InvalidOperationException("Rollback requires backup files from a previous apply.");
        }

        var restoredFiles = new List<string>();

        foreach (var backupFile in applyResult.BackupFiles)
        {
            if (string.IsNullOrWhiteSpace(backupFile))
            {
                continue;
            }

            var backupRelativePath = backupFile.Replace('\\', '/').Trim();
            var backupFullPath = Path.GetFullPath(Path.Combine(rootPath, backupRelativePath));
            var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!backupFullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Backup file is outside the project root: {backupFile}");
            }

            if (!File.Exists(backupFullPath))
            {
                throw new InvalidOperationException($"Backup file does not exist: {backupFile}");
            }

            var restoredRelativePath = GetRestoredRelativePathFromBackup(backupRelativePath);
            var deleteCreatedFile = restoredRelativePath.EndsWith(CreatedFileBackupSuffix, StringComparison.OrdinalIgnoreCase);

            if (deleteCreatedFile)
            {
                restoredRelativePath = restoredRelativePath[..^CreatedFileBackupSuffix.Length];
            }

            var restoredFullPath = Path.GetFullPath(Path.Combine(rootPath, restoredRelativePath));

            if (!restoredFullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Restored path is outside the project root: {restoredRelativePath}");
            }

            if (deleteCreatedFile)
            {
                if (File.Exists(restoredFullPath))
                {
                    File.Delete(restoredFullPath);
                }

                restoredFiles.Add(restoredRelativePath.Replace('\\', '/'));
                continue;
            }

            var restoredDirectory = Path.GetDirectoryName(restoredFullPath);
            if (!string.IsNullOrWhiteSpace(restoredDirectory))
            {
                Directory.CreateDirectory(restoredDirectory);
            }

            File.Copy(backupFullPath, restoredFullPath, overwrite: true);
            restoredFiles.Add(restoredRelativePath.Replace('\\', '/'));
        }

        var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");
        var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
        var testResult = await RunFixedCommandAsync(rootPath, "dotnet", ["test"], "dotnet test");

        var verificationResults = new[] { diffStatResult, buildResult, testResult };
        var verificationPassed = verificationResults.All(result => result.ExitCode == 0);

        return new LocalCoderPatchRollbackResult
        {
            RolledBack = true,
            VerificationPassed = verificationPassed,
            Message = verificationPassed
                ? $"Rollback restored {restoredFiles.Count} file(s) and verification passed."
                : $"Rollback restored {restoredFiles.Count} file(s), but verification failed. Review the verification results.",
            RestoredFiles = restoredFiles,
            VerificationResults = verificationResults
        };
    }

    public async Task<IReadOnlyList<CommandRunResult>> CommitChangesAsync(string projectPath, string commitMessage)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            throw new InvalidOperationException("Commit message is required.");
        }

        var results = new List<CommandRunResult>();

        var statusResult = await RunFixedCommandAsync(rootPath, "git", ["status", "--short"], "git status --short");
        results.Add(statusResult);

        if (statusResult.ExitCode != 0 || string.IsNullOrEmpty(statusResult.Output))
        {
            return results;
        }

        var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
        results.Add(buildResult);

        if (buildResult.ExitCode != 0)
        {
            return results;
        }

        var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");
        results.Add(diffStatResult);

        var addResult = await RunFixedCommandAsync(rootPath, "git", ["add", "."], "git add .");
        results.Add(addResult);

        if (addResult.ExitCode != 0)
        {
            return results;
        }

        var commitResult = await RunFixedCommandAsync(
            rootPath,
            "git",
            ["commit", "-m", commitMessage],
            "git commit -m <commit message>");
        results.Add(commitResult);

        if (commitResult.ExitCode != 0)
        {
            return results;
        }

        var deployResult = await RunFixedCommandAsync(
            rootPath,
            "docker",
            ["compose", "up", "-d", "--build", "aibox-devportal"],
            "docker compose up -d --build aibox-devportal");
        results.Add(deployResult);

        return results;
    }

    public async Task<IReadOnlyList<CommandRunResult>> BuildDeployAsync(string projectPath)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);

        var results = new List<CommandRunResult>();

        var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
        results.Add(buildResult);

        if (buildResult.ExitCode != 0)
        {
            return results;
        }

        var statusResult = await RunFixedCommandAsync(rootPath, "git", ["status", "--short"], "git status --short");
        results.Add(statusResult);

        var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");
        results.Add(diffStatResult);

        var deployResult = await RunFixedCommandAsync(
            rootPath,
            "docker",
            ["compose", "up", "-d", "--build", "aibox-devportal"],
            "docker compose up -d --build aibox-devportal");
        results.Add(deployResult);

        return results;
    }

    public async Task<CommandRunResult> RunCommandAsync(string projectPath, string command)
    {
        ValidateProjectPath(projectPath);
        ValidateCommand(command);

        var result = new CommandRunResult
        {
            Command = command,
            Started = DateTimeOffset.UtcNow
        };

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-lc \"{EscapeBash(command)}\"",
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Could not start process.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            result.ExitCode = -1;
            result.Error = "Command timed out after 5 minutes.";
            result.Finished = DateTimeOffset.UtcNow;
            return result;
        }

        result.Output = await outputTask;
        result.Error = await errorTask;
        result.ExitCode = process.ExitCode;
        result.Finished = DateTimeOffset.UtcNow;

        return result;
    }

    private static async Task<CommandRunResult> RunFixedCommandAsync(
        string projectPath,
        string executable,
        IReadOnlyList<string> arguments,
        string displayCommand)
    {
        var result = new CommandRunResult
        {
            Command = displayCommand,
            Started = DateTimeOffset.UtcNow
        };

        var processStartInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"Could not start fixed command: {displayCommand}");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            result.ExitCode = -1;
            result.Error = "Command timed out after 10 minutes.";
            result.Finished = DateTimeOffset.UtcNow;
            return result;
        }

        result.Output = await outputTask;
        result.Error = await errorTask;
        result.ExitCode = process.ExitCode;
        result.Finished = DateTimeOffset.UtcNow;
        return result;
    }

    public async Task<IReadOnlyList<ConsoleLocalCoderTask>> GetHistoryAsync()
    {
        await FileLock.WaitAsync();

        try
        {
            var path = GetHistoryPath();

            if (!File.Exists(path))
            {
                return [];
            }

            await using var stream = File.OpenRead(path);
            var tasks = await JsonSerializer.DeserializeAsync<List<ConsoleLocalCoderTask>>(stream, JsonOptions);
            return tasks?.OrderByDescending(x => x.Created).ToArray() ?? [];
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task SaveTaskAsync(ConsoleLocalCoderTask task)
    {
        await FileLock.WaitAsync();

        try
        {
            var path = GetHistoryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tasks = new List<ConsoleLocalCoderTask>();

            if (File.Exists(path))
            {
                await using var readStream = File.OpenRead(path);
                tasks = await JsonSerializer.DeserializeAsync<List<ConsoleLocalCoderTask>>(readStream, JsonOptions) ?? [];
            }

            tasks.Add(task);

            await using var writeStream = File.Create(path);
            await JsonSerializer.SerializeAsync(writeStream, tasks, JsonOptions);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private void ValidateProjectPath(string projectPath)
    {
        _ = GetValidatedProjectRoot(projectPath);
    }

    private void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Command is required.");
        }

        var allowedCommands = configuration
            .GetSection("AiBox:LocalCoder:AllowedCommands")
            .Get<string[]>() ?? [];

        var allowed = allowedCommands.Any(allowed =>
            command.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith(allowed + " ", StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            throw new InvalidOperationException($"Command is not allowed: {command}");
        }

        string[] blocked =
        [
            " rm ",
            " sudo ",
            " chmod ",
            " chown ",
            " docker ",
            " mkfs ",
            " dd ",
            " shutdown ",
            " reboot ",
            " curl ",
            " wget ",
            " > ",
            " >> "
        ];

        var padded = $" {command} ";

        if (blocked.Any(x => padded.Contains(x, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Command contains blocked token: {command}");
        }
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "coder-tasks.json");
    }

    private static string CreatePatchBackupDirectory(string projectPath)
    {
        var backupRoot = Path.Combine(
            projectPath,
            "Data",
            "patch-backups",
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));

        var candidate = backupRoot;
        var suffix = 1;

        while (Directory.Exists(candidate))
        {
            candidate = $"{backupRoot}-{suffix:00}";
            suffix++;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string GetCommandFailureMessage(CommandRunResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error)
            ? result.Output
            : result.Error;

        return $"{result.Command} exited {result.ExitCode}: {detail.Trim()}";
    }

    private static string GetRestoredRelativePathFromBackup(string backupRelativePath)
    {
        const string backupPrefix = "Data/patch-backups/";
        var normalizedBackupPath = backupRelativePath.Replace('\\', '/').Trim();

        if (!normalizedBackupPath.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Backup file is not under the expected backup folder: {backupRelativePath}");
        }

        var remainder = normalizedBackupPath[backupPrefix.Length..];
        var firstSeparator = remainder.IndexOf('/');

        if (firstSeparator < 0 || firstSeparator == remainder.Length - 1)
        {
            throw new InvalidOperationException($"Backup file path does not include a restorable file location: {backupRelativePath}");
        }

        return remainder[(firstSeparator + 1)..];
    }

    internal static string BuildCreatePlanPrompt(
        LocalCoderRequest request,
        string fileContextText,
        AgentModeProfile? profile = null,
        string agentInstructionsText = "")
    {
        var profileText = BuildAgentModeProfileText(profile);
        return $$"""
        You are a local coding assistant for a C# Blazor/Radzen project.

        {{profileText}}

        Project path:
        {{request.ProjectPath}}

        User task:
        {{request.Task}}

        Selected file count: {{request.FileContexts.Count}}

        Selected file context:
        {{fileContextText}}

        Relevant AGENTS.md files:
        {{agentInstructionsText}}

        If no files are selected, plan from the task text and AGENTS.md context instead of failing.
        Keep nullable reference usage consistent with the project.
        If you introduce a new service, include the required dependency injection registration.
        Prefer valid Razor expressions when the plan references UI changes.

        Produce:
        1. Short diagnosis.
        2. Files likely involved.
        3. Step-by-step implementation plan.
        4. Safe commands to verify.
        5. Do not invent files if unsure.
        6. Do not suggest destructive commands.

        Return plain markdown.
        """;
    }

    internal static string BuildGeneratePatchPreviewPrompt(
        LocalCoderRequest request,
        string selectedFilePathsText,
        string fileContextText,
        string scopeText,
        string intentText,
        IReadOnlyList<string>? targetCreatedFiles = null,
        bool xmlDocumentationMode = false,
        PatchPromptTargetResolution? targetResolution = null,
        PatchPreviewRepairContext? repairContext = null,
        AgentModeProfile? profile = null,
        string agentInstructionsText = "")
    {
        var profileText = BuildAgentModeProfileText(profile);
        var operationExample = xmlDocumentationMode
            ? """
        {
          "operations": [
            {
              "filePath": "<existing file>",
              "operation": "insert_before",
              "anchor": "<existing declaration>",
              "newText": "<new content>",
              "summary": "Add XML documentation"
            }
          ]
        }
        """
            : """
        {
          "operations": [
            {
              "filePath": "<target file>",
              "operation": "create",
              "oldText": "",
              "newText": "<new content>",
              "summary": "Create a new file"
            },
            {
              "filePath": "<existing file>",
              "operation": "insert_after",
              "anchor": "<existing anchor>",
              "oldText": "",
              "newText": "<new content>",
              "summary": "Insert content after an existing anchor"
            },
            {
              "filePath": "<existing file>",
              "operation": "remove",
              "anchor": "",
              "oldText": "<existing text>",
              "newText": "",
              "summary": "Remove existing text"
            }
          ]
        }
        """;
        var operationGrammar = """
        Allowed operation grammar:
        - create:
          - filePath required
          - newText required
          - oldText must be omitted or empty
        - replace:
          - filePath required
          - oldText required
          - newText required
          - oldText must be exact text from selected context
        - insert_before:
          - filePath required
          - anchor required
          - newText required
          - anchor must be exact text from selected context
        - insert_after:
          - filePath required
          - anchor required
          - newText required
          - anchor must be exact text from selected context
        - remove:
          - filePath required
          - oldText required
          - oldText must be exact text from selected context
        """;
        var forbiddenExamples = """
        Forbidden examples:
        - create with a filePath outside the project root
        - create outside a represented folder when scope is Context Files Only
        - replace with empty oldText
        - invented anchors
        - markdown explanations
        - code fences around JSON
        - changing files outside context
        """;
        var missingTextInstruction = """
        If exact oldText or anchor cannot be found, return:
        {
          "operations": [],
          "errors": [
            "Exact target text was not found in context."
          ]
        }
        """;
        var xmlDocumentationInstructions = xmlDocumentationMode
            ? """
        XML documentation mode is active.
        If XML documentation already exists, use replace with exact oldText.
        If XML documentation does not exist, use insert_before with the exact class or member declaration as anchor.
        Document every class, public member, and private method in the selected file unless it already has XML documentation.
        For each undocumented declaration, emit one insert_before operation.
        Do not stop after documenting the first declaration.
        Each insert_before anchor must be the exact declaration line from the selected context.
        Anchor text must be copied verbatim from the selected file context.
        Never reconstruct declarations.
        Never append '{' to the anchor unless '{' appears on the same line in the selected context.
        Use the entire declaration line exactly as shown.
        If exact declaration text cannot be identified, emit no operation.
        Each newText must end with a newline.
        Do not include the declaration in newText. Only include the XML documentation comment.
        This applies to brace-on-next-line declarations, brace-on-same-line declarations, generic return types, nullable return types, and primary constructor class declarations.
        Use exact text replacement for existing documentation comments and exact declaration anchors for new documentation.
        XML documentation comments must end with a newline before the member declaration.
        Example:
        /// <summary>
        /// Describes the member.
        /// </summary>
        public void ExistingMember()
        """
            : string.Empty;
        var repairInstructions = repairContext is null
            ? string.Empty
            : BuildRepairInstructionsText(repairContext);
        var targetResolutionText = BuildPromptTargetResolutionText(targetResolution);
        var hasTargetCreatedFiles = (targetCreatedFiles ?? []).Count > 0;
        var targetCreatedFilesText = hasTargetCreatedFiles
            ? $"""
        Target created file(s):
        {string.Join(Environment.NewLine, (targetCreatedFiles ?? []).Select(path => $"- {path}"))}
        """
            : string.Empty;
        var contextInstruction = hasTargetCreatedFiles
            ? """
        Use only files from the selected file context or Target created file(s).
        """
            : PatchIntentService.HasExplicitCreateRequest(request.Task)
            ? """
        Modify only selected context files. New files may be created only when explicitly requested and inside Allowed Create Folders.
        """
            : """
        Use only files from the selected file context.
        """;
        return $$"""
        You are a coding assistant generating a patch preview for a C# Blazor/Radzen project.

        Return ONLY JSON.
        Do not explain.
        Do not use markdown fences.
        Do not include git diff.
        {{repairInstructions}}
        Output format:
        {{operationExample}}
        {{operationGrammar}}
        Do not copy file paths from examples. Use only Allowed files and Target created file(s).
        {{forbiddenExamples}}
        {{missingTextInstruction}}
        {{contextInstruction}}
        Use exact anchors, exact string replacements, and exact text removal.
        {{xmlDocumentationInstructions}}

        {{profileText}}

        Project path:
        {{request.ProjectPath}}

        Task:
        {{request.Task}}

        Selected file count: {{request.FileContexts.Count}}

        Selected file paths:
        {{selectedFilePathsText}}

        Allowed patch scope:
        {{scopeText}}

        Patch intent:
        {{intentText}}

        {{targetCreatedFilesText}}

        {{targetResolutionText}}

        Selected file context:
        {{fileContextText}}

        Relevant AGENTS.md files:
        {{agentInstructionsText}}

        Keep nullable reference usage consistent with the project.
        Use valid Razor expressions for any UI edits.
        If you introduce a new service, include the required dependency injection registration.

        Generate the smallest safe patch operation set possible.
        """;
    }

    private static string BuildRepairInstructionsText(PatchPreviewRepairContext repairContext)
    {
        var validationErrors = string.Join(Environment.NewLine, repairContext.ValidationErrors.Select(error => $"- {error}"));
        var repairInstructions = BuildRepairCaseInstructionsText(repairContext);
        var replaceDiagnostics = repairContext.ReplaceDiagnostics.Count == 0
            ? "- None"
            : string.Join(
                Environment.NewLine,
                repairContext.ReplaceDiagnostics.Select(diagnostic =>
                {
                    var matches = diagnostic.ClosestMatches.Count == 0
                        ? "    - None"
                        : string.Join(
                            Environment.NewLine,
                            diagnostic.ClosestMatches.Select(match =>
                                $"    - Line {match.LineNumber}; Similarity {match.SimilarityScore:0.0}%; Snippet: {match.Snippet}"));

                    return $"""
                    - File: {diagnostic.FilePath}
                      Requested oldText: {diagnostic.RequestedOldText}
                      Suggested replacement target: {diagnostic.SuggestedReplacementTarget}
                      Closest matches:
                    {matches}
                    """.TrimEnd();
                }));

        var suggestedTargetDiagnostics = repairContext.SuggestedTargetDiagnostics.Count == 0
            ? "- None"
            : string.Join(
                Environment.NewLine,
                repairContext.SuggestedTargetDiagnostics.Select(diagnostic =>
                {
                    var matches = diagnostic.ClosestMatches.Count == 0
                        ? "    - None"
                        : string.Join(
                            Environment.NewLine,
                            diagnostic.ClosestMatches.Select(match =>
                                $"    - Line {match.LineNumber}; Similarity {match.SimilarityScore:0.0}%; Snippet: {match.Snippet}"));

                    return $"""
                    - File: {diagnostic.FilePath}
                      Failed operation: {diagnostic.FailedOperation}
                      Target field: {diagnostic.TargetLabel}
                      Requested text: {diagnostic.RequestedText}
                      Suggested target text: {diagnostic.SuggestedTargetText}
                      Closest matches:
                    {matches}
                    """.TrimEnd();
                }));

        return $"""
        Repair mode is active.
        Repair the failed patch preview using the current selected file context and the exact validation feedback below.
        Return corrected patch JSON only.
        {repairInstructions}
        Original user task:
        {repairContext.OriginalTask}

        Selected context files and file contents are listed below in the shared preview context section.

        Raw model response:
        {repairContext.RawModelResponse}

        Validation errors:
        {validationErrors}

        Closest-match suggestions from diagnostics:
        Replace diagnostics:
        {replaceDiagnostics}

        Suggested target diagnostics:
        {suggestedTargetDiagnostics}

        Patch operation grammar rules:
        - create:
          - filePath required
          - newText required
          - oldText must be omitted or empty
        - replace:
          - filePath required
          - oldText required
          - newText required
          - oldText must be exact text from selected context
        - insert_before:
          - filePath required
          - anchor required
          - newText required
          - anchor must be exact text from selected context
        - insert_after:
          - filePath required
          - anchor required
          - newText required
          - anchor must be exact text from selected context
        - remove:
          - filePath required
          - oldText required
          - oldText must be exact text from selected context

        Use the closest exact match when repairing oldText or anchor.
        """;
    }

    private static string BuildPromptTargetResolutionText(PatchPromptTargetResolution? targetResolution)
    {
        if (targetResolution is null)
        {
            return string.Empty;
        }

        return $"""
        Server-resolved target:
        Operation: {targetResolution.Operation}
        Target found: {(targetResolution.TargetFound ? "Yes" : "No")}
        File: {targetResolution.FilePath}
        Line: {targetResolution.LineNumber}
        Match count: {targetResolution.MatchCount}
        Exact target text:
        {targetResolution.TargetText}
        Surrounding context:
        {targetResolution.SurroundingContext}
        Do not search for anchors. Use the exact target text above.
        """;
    }

    private static bool HasMalformedReplaceValidationError(IReadOnlyList<string> validationErrors)
    {
        return validationErrors.Any(error =>
            error.Contains("replace", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("requires a non-empty oldText", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasMalformedRemoveValidationError(IReadOnlyList<string> validationErrors)
    {
        return validationErrors.Any(error =>
            error.Contains("remove", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("requires a non-empty oldText", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasMalformedCreateValidationError(IReadOnlyList<string> validationErrors)
    {
        return validationErrors.Any(error =>
            error.Contains("create", StringComparison.OrdinalIgnoreCase) &&
            (error.Contains("must include non-empty newText", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("must not include oldText", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasMalformedInsertAnchorValidationError(PatchPreviewValidationException exception)
    {
        return HasMalformedInsertAnchorValidationError(exception.ValidationErrors, exception.SuggestedTargetDiagnostics);
    }

    private static bool HasMalformedInsertAnchorValidationError(
        IReadOnlyList<string> validationErrors,
        IReadOnlyList<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics)
    {
        if (validationErrors.Any(error =>
                (error.Contains("insert_before", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("insert_after", StringComparison.OrdinalIgnoreCase)) &&
                error.Contains("requires a non-empty anchor", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return validationErrors.Any(error =>
            error.Contains("Anchor not found for insert_before", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Anchor not found for insert_after", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("make the insert task more specific", StringComparison.OrdinalIgnoreCase)) ||
            suggestedTargetDiagnostics.Any(diagnostic =>
                diagnostic.TargetLabel.Equals("anchor", StringComparison.OrdinalIgnoreCase) &&
                (diagnostic.FailedOperation.Equals("insert_before", StringComparison.OrdinalIgnoreCase) ||
                 diagnostic.FailedOperation.Equals("insert_after", StringComparison.OrdinalIgnoreCase)));
    }

    private static string BuildRepairCaseInstructionsText(PatchPreviewRepairContext repairContext)
    {
        if (HasMalformedCreateValidationError(repairContext.ValidationErrors))
        {
            return """
        Create operations require non-empty newText.
        Regenerate using the exact requested target file and create content from the original intent.

        Return JSON only.
        """;
        }

        if (HasMalformedReplaceValidationError(repairContext.ValidationErrors))
        {
            return """
        Replace operations require oldText.
        Regenerate using exact oldText from the selected file context.
        If a valid anchor is available, you may convert the operation to insert_before or insert_after.

        Return JSON only.
        """;
        }

        if (HasMalformedInsertAnchorValidationError(repairContext.ValidationErrors, repairContext.SuggestedTargetDiagnostics))
        {
            return """
        Insert operations require an exact anchor.
        Use the closest-match suggestion as the anchor.
        Regenerate with the exact anchor text from context.

        Return JSON only.
        """;
        }

        if (HasMalformedRemoveValidationError(repairContext.ValidationErrors))
        {
            return """
        Remove operations require oldText.
        Regenerate using exact oldText from the selected file context.

        Return JSON only.
        """;
        }

        return string.Empty;
    }

    private static string GetRepairAttemptLabel(PatchPreviewValidationException exception)
    {
        if (HasMalformedCreateValidationError(exception.ValidationErrors))
        {
            return "create";
        }

        if (HasMalformedReplaceValidationError(exception.ValidationErrors))
        {
            return "replace";
        }

        if (HasMalformedRemoveValidationError(exception.ValidationErrors))
        {
            return "remove";
        }

        var insertDiagnostic = exception.SuggestedTargetDiagnostics.FirstOrDefault(diagnostic =>
            diagnostic.TargetLabel.Equals("anchor", StringComparison.OrdinalIgnoreCase) &&
            (diagnostic.FailedOperation.Equals("insert_before", StringComparison.OrdinalIgnoreCase) ||
             diagnostic.FailedOperation.Equals("insert_after", StringComparison.OrdinalIgnoreCase)));

        if (insertDiagnostic is not null)
        {
            return insertDiagnostic.FailedOperation;
        }

        if (exception.ValidationErrors.Any(error => error.Contains("insert_after", StringComparison.OrdinalIgnoreCase)))
        {
            return "insert_after";
        }

        return "insert_before";
    }

    private static bool TryGetPatchPreviewRepairPlan(PatchPreviewValidationException exception, out PatchPreviewRepairPlan repairPlan)
    {
        if (HasMalformedReplaceValidationError(exception.ValidationErrors))
        {
            repairPlan = new PatchPreviewRepairPlan("replace", "replace");
            return true;
        }

        if (HasMalformedInsertAnchorValidationError(exception))
        {
            var repairAttempt = GetRepairAttemptLabel(exception);
            repairPlan = new PatchPreviewRepairPlan(repairAttempt, repairAttempt);
            return true;
        }

        if (HasMalformedRemoveValidationError(exception.ValidationErrors))
        {
            repairPlan = new PatchPreviewRepairPlan("remove", "remove");
            return true;
        }

        repairPlan = new PatchPreviewRepairPlan(string.Empty, string.Empty);
        return false;
    }

    private async Task<PatchPreviewRepairResult?> TryRepairPatchPreviewAsync(
        LocalCoderRequest request,
        AgentModeProfile? profile,
        PatchIntent intent,
        string intentText,
        string selectedFilePathsText,
        string fileContextText,
        string scopeText,
        bool xmlDocumentationMode,
        PatchPromptTargetResolution? targetResolution,
        PatchPreviewRepairPlan repairPlan,
        PatchPreviewValidationException originalException,
        string agentInstructionsText)
    {
        var repairContext = new PatchPreviewRepairContext(
            request.Task,
            originalException.RawModelResponse,
            originalException.ValidationErrors.ToArray(),
            originalException.ReplaceDiagnostics.ToArray(),
            originalException.SuggestedTargetDiagnostics.ToArray());

        return await patchPreviewRepairService.RepairAsync(
            request,
            intent,
            intentText,
            selectedFilePathsText,
            fileContextText,
            scopeText,
            xmlDocumentationMode,
            targetResolution,
            repairContext,
            agentInstructionsText,
            repairPlan.OriginalOperation,
            repairPlan.RepairAttempt,
            profile);
    }

    private void RecordPatchPreviewAttempt()
    {
        Interlocked.Increment(ref patchPreviewAttempts);
    }

    private void RecordPatchPreviewSuccess()
    {
        Interlocked.Increment(ref patchPreviewSuccessfulPreviews);
    }

    private void RecordPatchPreviewFailure()
    {
        Interlocked.Increment(ref patchPreviewFailedPreviews);
    }

    private void RecordPatchPreviewRepaired()
    {
        Interlocked.Increment(ref patchPreviewRepairedPreviews);
    }

    private sealed record PatchPreviewRepairPlan(string OriginalOperation, string RepairAttempt);

    private sealed record DeterministicTargetInstruction(string Operation, string TargetText);

    private sealed record DeterministicTargetMatch(
        string FilePath,
        int LineNumber,
        string TargetText,
        string SurroundingContext);

    internal static bool IsXmlDocumentationRequest(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return false;
        }

        return ContainsTaskPhrase(task, "xml documentation") ||
               ContainsTaskPhrase(task, "xml comments") ||
               ContainsTaskPhrase(task, "documentation comments");
    }

    private static bool ContainsTaskPhrase(string task, string phrase)
    {
        return task.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateXmlDocumentationOperationModes(string rawResponse)
    {
        var normalizedResponse = NormalizePatchPreviewModelResponse(rawResponse);
        if (string.IsNullOrWhiteSpace(normalizedResponse))
        {
            return;
        }

        using var document = JsonDocument.Parse(normalizedResponse);
        if (!document.RootElement.TryGetProperty("operations", out var operations) ||
            operations.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var forbiddenOperations = operations
            .EnumerateArray()
            .Select(operation => operation.TryGetProperty("operation", out var operationName) ? operationName.GetString() ?? string.Empty : string.Empty)
            .Where(operationName =>
                !string.Equals(operationName, "replace", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(operationName, "insert_before", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (forbiddenOperations.Length > 0)
        {
            throw new PatchPreviewValidationException(
                "XML documentation requests must use replace or insert_before operations.",
                ["XML documentation mode detected. The patch preview used an unsupported operation. Use replace when XML docs already exist, otherwise use insert_before with the class or member declaration as anchor."],
                rawResponse,
                string.Empty);
        }
    }

    private static string NormalizePatchPreviewModelResponse(string rawResponse)
    {
        var trimmed = (rawResponse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var fencedBlock = TryExtractMarkdownFence(trimmed);
        return fencedBlock ?? trimmed;
    }

    private static string? TryExtractMarkdownFence(string text)
    {
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0)
        {
            return null;
        }

        var openingLineEnd = text.IndexOf('\n', fenceStart);
        if (openingLineEnd < 0)
        {
            return null;
        }

        var openingFence = text[fenceStart..openingLineEnd].TrimEnd('\r').Trim();
        if (!openingFence.Equals("```", StringComparison.Ordinal) &&
            !openingFence.Equals("```json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var closingFenceStart = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceStart <= openingLineEnd)
        {
            return null;
        }

        var closingLineStart = text.LastIndexOf('\n', closingFenceStart);
        if (closingLineStart < 0)
        {
            return null;
        }

        var closingLineEnd = text.IndexOf('\n', closingFenceStart);
        closingLineEnd = closingLineEnd < 0 ? text.Length : closingLineEnd;

        var closingFence = text[(closingLineStart + 1)..closingLineEnd].TrimEnd('\r').Trim();
        if (!closingFence.Equals("```", StringComparison.Ordinal))
        {
            return null;
        }

        return text[(openingLineEnd + 1)..closingLineStart].Trim();
    }

    internal static string BuildAgentModeProfileText(AgentModeProfile? profile)
    {
        if (profile is null)
        {
            return string.Empty;
        }

        return $"""
        Agent mode profile:
        Name: {profile.Name}
        Mode: {profile.Mode}
        Model: {profile.Model}
        Rules summary: {profile.RulesSummary}
        """;
    }

    private string GetValidatedProjectRoot(string projectPath)
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

        var allowed = GetAllowedWorkspaceRoots()
            .Any(root => IsPathWithinRoot(fullPath, root));

        if (!allowed)
        {
            throw new InvalidOperationException("Project path is outside allowed workspace roots.");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private string[] GetAllowedWorkspaceRoots()
    {
        var configuredRoots = configuration
            .GetSection("AiBox:LocalCoder:WorkspaceRoots")
            .Get<string[]>() ?? [];
        var projectsRoot = configuration["DevPortal:ProjectsRoot"];

        return configuredRoots
            .Append(projectsRoot ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetProjectRoots(string root)
    {
        if (IsProjectRoot(root))
        {
            yield return root;
            yield break;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (IsProjectRoot(directory))
            {
                yield return Path.GetFullPath(directory);
            }
        }
    }

    private static bool IsProjectRoot(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, ".git")) ||
                   Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
                   Directory.EnumerateFiles(path, "*.sln", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        return normalizedPath.Equals(normalizedRoot, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string rootPath)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var currentDirectory = stack.Pop();

            foreach (var filePath in Directory.EnumerateFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileInfo = new FileInfo(filePath);

                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (IsAllowedProjectFile(filePath))
                {
                    yield return filePath;
                }
            }

            foreach (var directoryPath in Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var directoryInfo = new DirectoryInfo(directoryPath);

                if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (!IgnoredDirectoryNames.Contains(directoryInfo.Name))
                {
                    stack.Push(directoryPath);
                }
            }
        }
    }

    private static bool IsAllowedProjectFile(string filePath)
    {
        return AllowedProjectFileExtensions.Contains(Path.GetExtension(filePath));
    }

    private static string ValidateAndNormalizePatchTargetPath(string rootPath, string relativePath)
    {
        var trimmed = relativePath.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            throw new InvalidOperationException("Patch file paths must be repository-relative.");
        }

        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Patch file path '{trimmed}' must not contain a drive separator.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Patch file path '{trimmed}' cannot contain '..'.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, trimmed));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Patch file path '{trimmed}' is outside the project root.");
        }

        return Path.GetRelativePath(rootPath, fullPath);
    }

    private static string BuildFileContextText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file context was provided.";
        }

        var builder = new System.Text.StringBuilder();

        foreach (var fileContext in fileContexts)
        {
            builder.AppendLine($"FILE: {fileContext.RelativePath}");
            builder.AppendLine("```text");
            builder.AppendLine(TrimForPrompt(fileContext.Content, MaxPromptFileCharacters));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildPatchPreviewFileContextText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file context was provided. No files are currently loaded.";
        }

        var builder = new System.Text.StringBuilder();

        foreach (var fileContext in fileContexts)
        {
            builder.AppendLine($"FILE: {fileContext.RelativePath}");
            builder.AppendLine("```text");
            builder.AppendLine(TrimForPrompt(fileContext.Content, MaxPromptFileCharacters));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildAgentInstructionsText(AgentInstructionContext instructionContext)
    {
        if (!instructionContext.HasFiles)
        {
            return "No relevant AGENTS.md files were found.";
        }

        return instructionContext.CombinedText;
    }

    private AgentInstructionService GetAgentInstructionService()
    {
        return agentInstructionService ?? new AgentInstructionService(environment);
    }

    internal static PatchPromptAudit BuildPatchPromptAudit(
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string fileContextText)
    {
        var contextText = fileContextText ?? string.Empty;

        return new PatchPromptAudit
        {
            ContextFiles = fileContexts.Select(fileContext => fileContext.RelativePath).ToArray(),
            ContextCharactersLoaded = fileContexts.Sum(fileContext => fileContext.CharacterCount),
            ContextTextCharactersSent = contextText.Length,
            ContextTextFirst500Chars = contextText.Length <= 500 ? contextText : contextText[..500],
            ContextTextLast500Chars = contextText.Length <= 500 ? contextText : contextText[^500..]
        };
    }

    private static string BuildSelectedFilePathsText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file paths were provided.";
        }

        return string.Join(Environment.NewLine, fileContexts.Select(fileContext => $"- {fileContext.RelativePath}"));
    }

    internal static string BuildPatchScopeText(
        PatchScopeMode scopeMode,
        IReadOnlyList<string> allowedFolders,
        IReadOnlyList<string> allowedCreateFolders,
        bool useTargetCreatedFilesLabel = false)
    {
        return scopeMode switch
        {
            PatchScopeMode.ContextFilesOnly => useTargetCreatedFilesLabel ? "Context Files + Target Created Files" : "Context Files Only",
            PatchScopeMode.SelectedFolders when allowedFolders.Count > 0 => string.Join(
                Environment.NewLine,
                [
                    "Selected Folders",
                    "Allowed folders:",
                    string.Join(Environment.NewLine, allowedFolders.Select(folder => $"- {folder}"))
                ]),
            PatchScopeMode.SelectedFolders => "Selected Folders (no folders configured)",
            PatchScopeMode.AnyProjectFile => "Any Project File",
            _ => "Context Files Only"
        } + (allowedCreateFolders.Count > 0
            ? $"{Environment.NewLine}{Environment.NewLine}Allowed Create Folders:{Environment.NewLine}{string.Join(Environment.NewLine, allowedCreateFolders.Select(folder => $"- {folder}"))}"
            : string.Empty);
    }

    internal static PatchPromptTargetResolution? ResolveDeterministicTargetResolution(
        string task,
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        out string? validationError)
    {
        validationError = null;

        if (!TryParseDeterministicTargetInstruction(task, out var instruction))
        {
            return null;
        }

        var matches = FindDeterministicTargetMatches(fileContexts, instruction.TargetText).ToArray();
        if (matches.Length == 0)
        {
            validationError = $"Deterministic target text was not found in selected context for operation '{instruction.Operation}': {instruction.TargetText}";
            return new PatchPromptTargetResolution
            {
                TargetFound = false,
                Operation = instruction.Operation,
                TargetText = instruction.TargetText,
                MatchCount = 0,
                ErrorMessage = validationError
            };
        }

        if (matches.Length > 1)
        {
            validationError = $"Deterministic target text matched multiple locations in selected context for operation '{instruction.Operation}'. Make the task more specific.";
            return new PatchPromptTargetResolution
            {
                TargetFound = false,
                Operation = instruction.Operation,
                TargetText = instruction.TargetText,
                MatchCount = matches.Length,
                ErrorMessage = validationError
            };
        }

        var match = matches[0];
        return new PatchPromptTargetResolution
        {
            TargetFound = true,
            Operation = instruction.Operation,
            FilePath = match.FilePath,
            LineNumber = match.LineNumber,
            TargetText = match.TargetText,
            SurroundingContext = match.SurroundingContext,
            MatchCount = 1
        };
    }

    private static bool TryParseDeterministicTargetInstruction(string task, out DeterministicTargetInstruction instruction)
    {
        var lines = (task ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n');

        if (TryReadTaskBlockBetweenLabels(lines, "replace", "with", out var replaceText))
        {
            instruction = new DeterministicTargetInstruction("replace", replaceText);
            return true;
        }

        if (TryReadTaskBlockAfterLabel(lines, "before", out var insertBeforeText))
        {
            instruction = new DeterministicTargetInstruction("insert_before", insertBeforeText);
            return true;
        }

        if (TryReadTaskBlockAfterLabel(lines, "after", out var insertAfterText))
        {
            instruction = new DeterministicTargetInstruction("insert_after", insertAfterText);
            return true;
        }

        if (TryReadTaskBlockAfterLabel(lines, "delete", out var deleteText))
        {
            instruction = new DeterministicTargetInstruction("delete", deleteText);
            return true;
        }

        if (TryReadTaskBlockAfterLabel(lines, "remove", out var removeText))
        {
            instruction = new DeterministicTargetInstruction("delete", removeText);
            return true;
        }

        instruction = new DeterministicTargetInstruction(string.Empty, string.Empty);
        return false;
    }

    private static bool TryReadTaskBlockBetweenLabels(string[] lines, string startLabel, string endLabel, out string text)
    {
        var startIndex = FindTaskLabelLineIndex(lines, startLabel);
        if (startIndex < 0)
        {
            text = string.Empty;
            return false;
        }

        var builder = new List<string>();
        AddInlineTaskLabelText(builder, lines[startIndex], startLabel, endLabel);

        for (var index = startIndex + 1; index < lines.Length; index++)
        {
            if (ContainsTaskLabel(lines[index], endLabel))
            {
                break;
            }

            if (IsTaskLabelLine(lines[index]) && builder.Count > 0)
            {
                break;
            }

            builder.Add(lines[index]);
        }

        text = NormalizeTaskBlockText(builder);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryReadTaskBlockAfterLabel(string[] lines, string label, out string text)
    {
        var labelIndex = FindTaskLabelLineIndex(lines, label);
        if (labelIndex < 0)
        {
            text = string.Empty;
            return false;
        }

        var builder = new List<string>();
        AddInlineTaskLabelText(builder, lines[labelIndex], label);

        for (var index = labelIndex + 1; index < lines.Length; index++)
        {
            if (IsTaskLabelLine(lines[index]) && builder.Count > 0)
            {
                break;
            }

            builder.Add(lines[index]);
        }

        text = NormalizeTaskBlockText(builder);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static int FindTaskLabelLineIndex(string[] lines, string label)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            if (ContainsTaskLabel(lines[index], label))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AddInlineTaskLabelText(List<string> builder, string line, string label, string? endLabel = null)
    {
        var labelIndex = line.IndexOf($"{label}:", StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0)
        {
            return;
        }

        var afterLabel = line[(labelIndex + label.Length + 1)..];
        if (!string.IsNullOrWhiteSpace(afterLabel))
        {
            if (!string.IsNullOrWhiteSpace(endLabel))
            {
                var endIndex = afterLabel.IndexOf($"{endLabel}:", StringComparison.OrdinalIgnoreCase);
                if (endIndex >= 0)
                {
                    afterLabel = afterLabel[..endIndex];
                }
            }

            builder.Add(afterLabel.TrimStart());
        }
    }

    private static bool ContainsTaskLabel(string line, string label)
    {
        return line.IndexOf($"{label}:", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTaskLabelLine(string line)
    {
        return line.Contains("replace:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("with:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("before:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("after:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("delete:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("remove:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTaskBlockText(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var joined = string.Join(Environment.NewLine, lines);
        return NormalizeTaskBlockText(joined);
    }

    private static string NormalizeTaskBlockText(string text)
    {
        var normalized = (text ?? string.Empty).ReplaceLineEndings(Environment.NewLine);
        var lines = normalized.ReplaceLineEndings("\n").Split('\n');

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (start > end)
        {
            return string.Empty;
        }

        var selected = lines[start..(end + 1)];
        var joined = string.Join(Environment.NewLine, selected);
        return TrimMatchingQuotes(joined);
    }

    private static string TrimMatchingQuotes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            {
                return trimmed[1..^1];
            }
        }

        return text.TrimEnd();
    }

    private static IEnumerable<DeterministicTargetMatch> FindDeterministicTargetMatches(
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string targetText)
    {
        var normalizedTarget = (targetText ?? string.Empty).ReplaceLineEndings("\n");

        foreach (var fileContext in fileContexts)
        {
            var normalizedContent = (fileContext.Content ?? string.Empty).ReplaceLineEndings("\n");
            var index = 0;

            while (index >= 0 && index < normalizedContent.Length)
            {
                index = normalizedContent.IndexOf(normalizedTarget, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                var lineNumber = CountLinesBeforeIndex(normalizedContent, index) + 1;
                var surroundingContext = BuildSurroundingContext(normalizedContent, lineNumber, normalizedTarget);
                yield return new DeterministicTargetMatch(
                    fileContext.RelativePath,
                    lineNumber,
                    normalizedTarget,
                    surroundingContext);
                index += Math.Max(1, normalizedTarget.Length);
            }
        }
    }

    private static int CountLinesBeforeIndex(string text, int index)
    {
        var count = 0;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string BuildSurroundingContext(string normalizedContent, int lineNumber, string targetText)
    {
        var lines = normalizedContent.Split('\n');
        var targetLineCount = Math.Max(1, (targetText.ReplaceLineEndings("\n").Split('\n').Length));
        var startIndex = Math.Max(0, lineNumber - 3);
        var endIndex = Math.Min(lines.Length - 1, lineNumber - 1 + targetLineCount + 2);

        var builder = new System.Text.StringBuilder();
        for (var index = startIndex; index <= endIndex; index++)
        {
            builder.AppendLine($"{index + 1}: {lines[index]}");
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryParseExactReplacementTask(string task, out string oldText, out string newText)
    {
        var match = Regex.Match(
            task,
            "replace\\s+this\\s+exact\\s+text:\\s*\"(?<old>.*?)\"\\s*with:\\s*\"(?<new>.*?)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            oldText = string.Empty;
            newText = string.Empty;
            return false;
        }

        oldText = match.Groups["old"].Value;
        newText = match.Groups["new"].Value;
        return true;
    }

    private static bool TryParseExactRemovalTask(string task, out string oldText)
    {
        var match = Regex.Match(
            task,
            "remove\\s+this\\s+exact\\s+text:\\s*\"(?<old>.*?)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            oldText = string.Empty;
            return false;
        }

        oldText = match.Groups["old"].Value;
        return true;
    }

    private static string BuildExactReplacementPatch(
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string oldText,
        string newText)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            throw new InvalidOperationException("Old text must not be empty.");
        }

        var matches = fileContexts
            .Select(fileContext => new
            {
                FileContext = fileContext,
                MatchCount = CountExactOccurrences(fileContext.Content, oldText)
            })
            .Where(match => match.MatchCount > 0)
            .ToArray();

        var totalMatchCount = matches.Sum(match => match.MatchCount);
        if (totalMatchCount == 0)
        {
            throw new InvalidOperationException("Old text was not found in selected files.");
        }

        if (totalMatchCount > 1)
        {
            throw new InvalidOperationException("Old text appears multiple times; make the task more specific.");
        }

        var fileContext = matches.Single().FileContext;
        var relativePath = NormalizePreviewPath(fileContext.RelativePath);
        var updatedContent = fileContext.Content.Replace(oldText, newText, StringComparison.Ordinal);
        var oldLines = fileContext.Content.ReplaceLineEndings("\n").Split('\n');
        var newLines = updatedContent.ReplaceLineEndings("\n").Split('\n');

        var commonPrefixCount = 0;
        while (commonPrefixCount < oldLines.Length &&
               commonPrefixCount < newLines.Length &&
               string.Equals(oldLines[commonPrefixCount], newLines[commonPrefixCount], StringComparison.Ordinal))
        {
            commonPrefixCount++;
        }

        var commonSuffixCount = 0;
        while (commonSuffixCount < oldLines.Length - commonPrefixCount &&
               commonSuffixCount < newLines.Length - commonPrefixCount &&
               string.Equals(
                   oldLines[oldLines.Length - commonSuffixCount - 1],
                   newLines[newLines.Length - commonSuffixCount - 1],
                   StringComparison.Ordinal))
        {
            commonSuffixCount++;
        }

        const int contextLineCount = 3;
        var hunkStartIndex = Math.Max(0, commonPrefixCount - contextLineCount);
        var oldChangedEndIndex = oldLines.Length - commonSuffixCount;
        var newChangedEndIndex = newLines.Length - commonSuffixCount;
        var oldHunkEndIndex = Math.Min(oldLines.Length, oldChangedEndIndex + contextLineCount);
        var newHunkEndIndex = Math.Min(newLines.Length, newChangedEndIndex + contextLineCount);
        var oldHunkLineCount = oldHunkEndIndex - hunkStartIndex;
        var newHunkLineCount = newHunkEndIndex - hunkStartIndex;
        var hunkStartLine = hunkStartIndex + 1;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"diff --git a/{relativePath} b/{relativePath}");
        builder.AppendLine($"--- a/{relativePath}");
        builder.AppendLine($"+++ b/{relativePath}");
        builder.AppendLine($"@@ -{hunkStartLine},{oldHunkLineCount} +{hunkStartLine},{newHunkLineCount} @@");

        for (var index = hunkStartIndex; index < commonPrefixCount; index++)
        {
            builder.Append(' ').AppendLine(oldLines[index]);
        }

        for (var index = commonPrefixCount; index < oldChangedEndIndex; index++)
        {
            builder.Append('-').AppendLine(oldLines[index]);
        }

        for (var index = commonPrefixCount; index < newChangedEndIndex; index++)
        {
            builder.Append('+').AppendLine(newLines[index]);
        }

        for (var index = oldChangedEndIndex; index < oldHunkEndIndex; index++)
        {
            builder.Append(' ').AppendLine(oldLines[index]);
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> FindExactReplacementTargetPaths(
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string oldText)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return [];
        }

        var matches = fileContexts
            .Where(fileContext => CountExactOccurrences(fileContext.Content, oldText) > 0)
            .Select(fileContext => NormalizePreviewPath(fileContext.RelativePath))
            .ToArray();

        return matches.Length == 1 ? matches : [];
    }

    private static int CountExactOccurrences(string content, string value)
    {
        var count = 0;
        var startIndex = 0;

        while ((startIndex = content.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private static LocalCoderPatchValidationResult ValidatePatchPreview(
        string patchText,
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string task,
        PatchIntent? intent = null,
        IReadOnlyList<PatchFileChange>? fileChanges = null)
    {
        if (string.IsNullOrWhiteSpace(patchText))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview response was empty."]
            };
        }

        var selectedPaths = new HashSet<string>(
    fileContexts.Select(fileContext => NormalizePreviewPath(fileContext.RelativePath)),
    StringComparer.OrdinalIgnoreCase);

        var changedPaths = fileChanges?
            .Select(change => NormalizePreviewPath(change.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var createOnlyPatch =
            changedPaths.Length > 0 &&
            fileChanges is not null &&
            fileChanges.All(change => string.IsNullOrEmpty(change.OldContent));

        var allowedCreateFolders = intent?.AllowedCreateFolders?
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(NormalizePreviewFolder)
            .ToArray() ?? [];

        var createTargetsAreAllowed =
            createOnlyPatch &&
            allowedCreateFolders.Length > 0 &&
            changedPaths.All(path => allowedCreateFolders.Any(folder =>
                path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));

        if (selectedPaths.Count == 0 && !createTargetsAreAllowed)
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview requires selected file context."]
            };
        }

        if (patchText.Contains("Short Diagnosis", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Files Likely Involved", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Step-by-Step Implementation Plan", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Safe Commands to Verify", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Example Implementation", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Here's how", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Here’s how", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("path/to/file", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("replace with actual values", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("actual values from your project", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("GeneratePatchPreviewButton", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("MyComponent", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("ExampleComponent", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("SampleComponent", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("PlaceholderComponent", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview contains forbidden plan or placeholder text."]
            };
        }

        if (string.Equals(patchText, "PATCH_NOT_POSSIBLE", StringComparison.Ordinal))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = true
            };
        }

        if (!patchText.StartsWith("diff --git", StringComparison.Ordinal))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview must start with diff --git or PATCH_NOT_POSSIBLE."]
            };
        }

        var errors = new List<string>();
        var lines = patchText
            .ReplaceLineEndings("\n")
            .Split('\n');

        if (!IsValidUnifiedDiff(lines))
        {
            errors.Add("Patch preview contains text outside a valid unified diff.");
        }

        if (!HasRealChangedLine(lines))
        {
            errors.Add("Patch preview must include at least one real changed line.");
        }

        if (HasOnlyClosingTagChanges(lines))
        {
            errors.Add("Patch preview only changes closing tags.");
        }

        var fileDiffs = ExtractChangedFilePathsFromDiffHeaders(lines).ToArray();

        if (HasUnsafeRazorClosingTagChange(lines))
        {
            errors.Add("Patch preview may break Razor markup by changing closing tag type.");
        }

        var razorStructureValidation = RazorStructureGuard.Analyze(patchText, task);
        if (!razorStructureValidation.IsValid)
        {
            errors.AddRange(razorStructureValidation.Errors);
        }

        if (!PatchAppearsToImplementTask(task, lines))
        {
            errors.Add("Patch preview does not appear to implement the requested change.");
        }

        foreach (var fileDiff in fileDiffs)
        {
            var isCreateTarget = fileChanges is not null && fileChanges.Any(change =>
                string.Equals(NormalizePreviewPath(change.RelativePath), fileDiff.NewPath, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(change.OldContent) &&
                !string.IsNullOrWhiteSpace(change.NewContent));
            ValidatePreviewPath(
                fileDiff.OldPath,
                selectedPaths,
                intent,
                errors,
                allowUnselected: isCreateTarget,
                isCreateTarget: false);
            ValidatePreviewPath(
                fileDiff.NewPath,
                selectedPaths,
                intent,
                errors,
                allowUnselected: false,
                isCreateTarget: isCreateTarget);
        }

        return new LocalCoderPatchValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private static bool PatchAppearsToImplementTask(string task, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return true;
        }

        var addedLines = GetChangedContentLines(lines, '+');
        var removedLines = GetChangedContentLines(lines, '-');
        var quotedValues = ExtractQuotedTaskValues(task);
        var isRemovalTask = ContainsWholeWord(task, "remove") || ContainsWholeWord(task, "delete");

        if (quotedValues.Count > 0 &&
            (
                isRemovalTask
                    ? !removedLines.Any(line => line.Contains(quotedValues[^1], StringComparison.Ordinal))
                    : !addedLines.Any(line => line.Contains(quotedValues[^1], StringComparison.Ordinal))
            ))
        {
            return false;
        }

        if (!ContainsWholeWord(task, "subtitle"))
        {
            return true;
        }

        var changedLines = removedLines.Concat(addedLines).ToArray();
        if (changedLines.Length > 0 &&
            changedLines.All(line => line.Contains("<RadzenButton", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return HasRadzenTextChangeNearPageHeader(lines);
    }

    private static string[] GetChangedContentLines(IReadOnlyList<string> lines, char prefix)
    {
        var metadataPrefix = new string(prefix, 3);

        return lines
            .Where(line => line.Length > 0 &&
                line[0] == prefix &&
                !line.StartsWith(metadataPrefix, StringComparison.Ordinal))
            .Select(line => line[1..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractQuotedTaskValues(string task)
    {
        return Regex.Matches(task, "[\"']([^\"'\\r\\n]+)[\"']")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool HasRadzenTextChangeNearPageHeader(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var isChangedLine = (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)) ||
                                (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal));

            if (!isChangedLine || !line.Contains("<RadzenText", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = Math.Max(0, index - 12);
            var end = Math.Min(lines.Count - 1, index + 12);

            for (var nearbyIndex = start; nearbyIndex <= end; nearbyIndex++)
            {
                var nearbyLine = lines[nearbyIndex];
                if (nearbyLine.Contains("<PageTitle", StringComparison.OrdinalIgnoreCase) ||
                    nearbyLine.Contains("Text=\"Local Coder\"", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizePatchPreviewText(string patchText)
    {
        var cleaned = RemoveMarkdownFences(patchText);
        var diffStart = cleaned.IndexOf("diff --git", StringComparison.Ordinal);

        return diffStart >= 0 ? cleaned[diffStart..].Trim() : cleaned.Trim();
    }

    private static string RemoveMarkdownFences(string patchText)
    {
        var lines = (patchText ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("```", StringComparison.Ordinal);
            });

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static PatchPreviewValidationException CreatePatchPreviewValidationException(
        LocalCoderPatchValidationResult validation,
        string rawResponse,
        string normalizedDiff,
        string normalizedResponse)
    {
        return new PatchPreviewValidationException(
            $"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", validation.Errors)}",
            validation.Errors,
            rawResponse,
            normalizedDiff,
            normalizedResponse);
    }

    private async Task SavePatchDebugRawResponseAsync(string rawResponse)
    {
        var debugDirectory = Path.Combine(environment.ContentRootPath, "Data", "patch-debug");
        Directory.CreateDirectory(debugDirectory);

        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-raw.txt";
        var filePath = Path.Combine(debugDirectory, fileName);
        await File.WriteAllTextAsync(filePath, rawResponse ?? string.Empty);
    }

    private static bool IsValidUnifiedDiff(IReadOnlyList<string> lines)
    {
        var inHunk = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (!IsValidDiffMetadataLine(line))
                {
                    return false;
                }

                inHunk = false;
                continue;
            }

            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                if (!IsValidIndexMetadataLine(line))
                {
                    return false;
                }

                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("similarity index ", StringComparison.Ordinal)
                || line.StartsWith("dissimilarity index ", StringComparison.Ordinal)
                || line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal)
                || line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunk = true;
                continue;
            }

            if (inHunk && (line.StartsWith("+", StringComparison.Ordinal) || line.StartsWith("-", StringComparison.Ordinal) || line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal)))
            {
                continue;
            }

            return false;
        }

        return lines.Any(line => line.StartsWith("@@ ", StringComparison.Ordinal));
    }

    private static bool IsValidDiffMetadataLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var leftPath = StripDiffPathPrefix(parts[2]);
        var rightPath = StripDiffPathPrefix(parts[3]);

        return IsValidPreviewPathToken(leftPath) &&
               IsValidPreviewPathToken(rightPath);
    }

    private static bool IsValidIndexMetadataLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hashes = parts[1].Split("..", StringSplitOptions.RemoveEmptyEntries);
        if (hashes.Length != 2)
        {
            return false;
        }

        if (!IsAllowedDiffHash(hashes[0]) || !IsAllowedDiffHash(hashes[1]))
        {
            return false;
        }

        return parts.Length == 2 || parts[2].All(char.IsDigit);
    }

    private static bool IsValidPreviewPathToken(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Path.IsPathRooted(path) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedDiffHash(string token)
    {
        var cleaned = token.Trim();
        if (cleaned.Length < 7 || cleaned.Length > 40)
        {
            return false;
        }

        if (!cleaned.All(c => Uri.IsHexDigit(c)))
        {
            return false;
        }

        if (IsPlaceholderDiffHash(cleaned))
        {
            return false;
        }

        return true;
    }

    private static bool IsPlaceholderDiffHash(string token)
    {
        var lower = token.Trim().ToLowerInvariant();
        string[] placeholders =
        [
            "abcdef",
            "abcdef0",
            "1234567",
            "12345678",
            "89abcde",
            "0123456",
            "01234567",
            "deadbeef",
            "cafebabe",
            "feedface"
        ];

        return placeholders.Contains(lower, StringComparer.Ordinal) ||
               lower.Distinct().Count() == 1;
    }

    private static bool HasRealChangedLine(IReadOnlyList<string> lines)
    {
        return lines.Any(line =>
            ((line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)) ||
             (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))) &&
            !string.IsNullOrWhiteSpace(line[1..]));
    }

    private static bool HasUnsafeRazorClosingTagChange(IReadOnlyList<string> lines)
    {
        var removedTags = new List<string>();
        var addedTags = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                removedTags.Add(line[1..].Trim());
            }
            else if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                addedTags.Add(line[1..].Trim());
            }
        }

        if (removedTags.Count == 0 || addedTags.Count == 0)
        {
            return false;
        }

        if (removedTags.Any(line => line.Contains("</RadzenCard>", StringComparison.OrdinalIgnoreCase)) &&
            addedTags.Any(line => line.Contains("</div>", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var removedClosingTags = removedTags
            .SelectMany(ExtractClosingTags)
            .ToList();

        var addedClosingTags = addedTags
            .SelectMany(ExtractClosingTags)
            .ToList();

        if (removedClosingTags.Count == 0 || addedClosingTags.Count == 0)
        {
            return false;
        }

        if (removedClosingTags.Count != addedClosingTags.Count)
        {
            return true;
        }

        for (var i = 0; i < removedClosingTags.Count; i++)
        {
            if (!removedClosingTags[i].Equals(addedClosingTags[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOnlyClosingTagChanges(IReadOnlyList<string> lines)
    {
        var changedLines = lines
            .Where(line => (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                || (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal)))
            .Select(line => line[1..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (changedLines.Length == 0)
        {
            return false;
        }

        return changedLines.All(line => IsClosingTagOnlyLine(line));
    }

    private static bool IsClosingTagOnlyLine(string line)
    {
        if (!line.Contains("</", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = line.Replace("<", string.Empty, StringComparison.Ordinal)
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Trim();

        return !string.IsNullOrWhiteSpace(normalized) && !normalized.Contains(' ');
    }

    private static IEnumerable<string> ExtractClosingTags(string line)
    {
        var current = line;
        var index = 0;

        while (index >= 0 && index < current.Length)
        {
            index = current.IndexOf("</", index, StringComparison.Ordinal);
            if (index < 0)
            {
                yield break;
            }

            var end = current.IndexOf('>', index);
            if (end < 0)
            {
                yield break;
            }

            yield return current[(index + 2)..end].Trim();
            index = end + 1;
        }
    }

    private static void ValidatePreviewPath(
        string path,
        IReadOnlySet<string> selectedPaths,
        PatchIntent? intent,
        List<string> errors,
        bool allowUnselected,
        bool isCreateTarget)
    {
        if (string.IsNullOrWhiteSpace(path) || IsDevNullPath(path))
        {
            return;
        }

        if (Path.IsPathRooted(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            IsBlockedPreviewPath(path))
        {
            errors.Add($"Patch preview references an invalid file path: {path}");
            return;
        }

        if (selectedPaths.Contains(path))
        {
            return;
        }

        if (allowUnselected)
        {
            return;
        }

        if (isCreateTarget)
        {
            if (IsCreatedFileAllowedByPatchIntent(intent, path) && IsAllowedCreateFolder(intent, path))
            {
                return;
            }

            var parentFolder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            errors.Add(string.IsNullOrWhiteSpace(parentFolder)
                ? "Create folder not allowed. Add a folder to Allowed Create Folders."
                : $"Create folder not allowed. Add {parentFolder}/ to Allowed Create Folders.");
            return;
        }

        errors.Add($"Patch preview references a file path not present in the selected file context: {path}");
    }

    private static bool IsCreatedFileAllowedByPatchIntent(PatchIntent? intent, string path)
    {
        if (intent is null)
        {
            return false;
        }

        return intent.AllowedFiles.Any(filePath => string.Equals(NormalizePreviewPath(filePath), path, StringComparison.OrdinalIgnoreCase)) ||
               intent.TargetCreatedFiles.Any(filePath => string.Equals(NormalizePreviewPath(filePath), path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowedCreateFolder(PatchIntent? intent, string path)
    {
        if (intent is null)
        {
            return false;
        }

        var parentFolder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            return false;
        }

        var normalizedParent = parentFolder.EndsWith("/", StringComparison.Ordinal)
            ? parentFolder
            : $"{parentFolder}/";

        return intent.AllowedCreateFolders.Any(folder =>
            string.Equals(NormalizePreviewPath(folder), normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedParent.StartsWith(NormalizePreviewPath(folder), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlockedPreviewPath(string path)
    {
        var normalized = NormalizePreviewPath(path);
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DiffFilePaths> ExtractChangedFilePathsFromDiffHeaders(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    yield return new DiffFilePaths(
                        StripDiffPathPrefix(parts[2]),
                        StripDiffPathPrefix(parts[3]));
                }
            }
        }
    }

    private sealed record DiffFilePaths(string OldPath, string NewPath);

    private static bool IsPlanningOnlyTask(string task)
    {
        var normalized = task.Trim().ToLowerInvariant();

        string[] strongEditVerbs =
        [
            "add",
            "change",
            "update",
            "replace",
            "remove",
            "rename",
            "fix",
            "refactor",
            "move",
            "create",
            "delete",
            "modify"
        ];

        // A concrete edit verb always takes precedence, including mixed tasks such as
        // "Review the page and change the subtitle."
        if (strongEditVerbs.Any(verb => StartsWithWord(normalized, verb)) ||
            strongEditVerbs.Any(verb => ContainsWholeWord(normalized, verb)))
        {
            return false;
        }

        string[] planningWords =
        [
            "review",
            "suggest",
            "analyze",
            "explain",
            "plan",
            "recommend"
        ];

        string[] planningPhrases =
        [
            "give me a plan",
            "next safe improvement"
        ];

        return planningWords.Any(word => ContainsWholeWord(normalized, word)) ||
               planningPhrases.Any(normalized.Contains);
    }

    private static bool StartsWithWord(string text, string word)
    {
        return text.StartsWith(word, StringComparison.Ordinal) &&
               (text.Length == word.Length || !char.IsLetterOrDigit(text[word.Length]));
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.Ordinal);

        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

            if (beforeOk && afterOk)
            {
                return true;
            }

            index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal);
        }

        return false;
    }

    private static string StripDiffPathPrefix(string path)
    {
        var trimmed = path.Trim();

        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            return trimmed[2..];
        }

        return trimmed;
    }

    private static bool IsDevNullPath(string path)
    {
        return string.Equals(path.Replace('\\', '/').Trim(), "/dev/null", StringComparison.Ordinal);
    }

    private static string NormalizePreviewPath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
    private static string NormalizePreviewFolder(string path)
    {
        var normalized = NormalizePreviewPath(path);

        return normalized.EndsWith("/", StringComparison.Ordinal)
            ? normalized
            : $"{normalized}/";
    }
    private static string TrimForPrompt(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content ?? string.Empty;
        }

        return content[..maxChars];
    }

    private static string EscapeBash(string command)
    {
        return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel> Models { get; set; } = [];
    }

    private sealed class OllamaModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ChatCompletionMessage> Messages { get; set; } = [];

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class ChatCompletionMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatCompletionChoice> Choices { get; set; } = [];
    }

    private sealed class ChatCompletionChoice
    {
        [JsonPropertyName("message")]
        public ChatCompletionMessage? Message { get; set; }
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;
    }

    public async Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath, IReadOnlyList<string>? commands = null)
    {
        var results = new List<CommandRunResult>();

        var verificationCommands = (commands is { Count: > 0 }
            ? commands
            : new[]
            {
                "dotnet build"
            })
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.Trim())
            .ToArray();

        if (verificationCommands.Length == 0)
        {
            verificationCommands = ["dotnet build"];
        }

        foreach (var command in verificationCommands)
        {
            var result = await RunCommandAsync(projectPath, command);
            results.Add(result);
        }

        return results;
    }
}
