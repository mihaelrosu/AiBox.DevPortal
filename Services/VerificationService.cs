using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class VerificationService(
    IWorkflowRunHistoryService workflowRunHistoryService,
    IExecutionEngineService executionEngineService,
    IFileOperationService fileOperationService,
    IGitOperationService gitOperationService,
    IDockerOperationService dockerOperationService,
    IComfyUiOperationService comfyUiOperationService,
    IAgentRegistryService agentRegistryService,
    ILocalLlmService localLlmService) : IVerificationService
{
    private const string ResultsPath = "/data/verification-results.json";
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string[] DangerousCommands =
    [
        "rm -rf /",
        "mkfs",
        "dd if=",
        "shutdown",
        "reboot",
        "git reset --hard",
        "git clean -fd",
        "git push --force",
        "git rebase",
        "git checkout -- .",
        "git restore .",
        "git rm",
        "docker system prune",
        "docker volume prune",
        "docker volume rm",
        "docker rm -f",
        "docker rmi",
        "docker compose down -v",
        "docker compose rm"
    ];

    public async Task<VerificationResult> VerifyAsync(string runId, VerificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedRunId = string.IsNullOrWhiteSpace(runId) ? request.RunId?.Trim() ?? string.Empty : runId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRunId))
        {
            throw new ArgumentException("RunId is required.", nameof(runId));
        }
        if (!string.IsNullOrWhiteSpace(request.RunId) && !request.RunId.Equals(normalizedRunId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Request RunId does not match the route RunId.", nameof(request));
        }

        var run = await workflowRunHistoryService.GetByIdAsync(normalizedRunId, cancellationToken)
            ?? throw new KeyNotFoundException($"Workflow run '{normalizedRunId}' was not found.");

        var executions = (await executionEngineService.GetAllAsync(cancellationToken))
            .Where(item => item.RunId.Equals(normalizedRunId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var fileOperations = (await fileOperationService.GetForRunAsync(normalizedRunId, cancellationToken)).ToArray();
        var gitOperations = (await gitOperationService.GetForRunAsync(normalizedRunId, cancellationToken)).ToArray();
        var dockerOperations = (await dockerOperationService.GetForRunAsync(normalizedRunId, cancellationToken)).ToArray();
        var comfyUiOperations = (await comfyUiOperationService.GetForRunAsync(normalizedRunId, cancellationToken)).ToArray();

        var result = new VerificationResult
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = normalizedRunId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        AddRequiredStepsCheck(result.Checks, run);
        AddFailedOperationsCheck(result.Checks, executions, fileOperations, gitOperations, dockerOperations, comfyUiOperations);
        AddDangerousCommandsCheck(result.Checks, executions, gitOperations, dockerOperations);
        AddConfiguredCommandCheck(result.Checks, "Build command executed", run.ProjectSnapshot?.BuildCommand, executions);
        AddConfiguredCommandCheck(result.Checks, "Test command executed", run.ProjectSnapshot?.TestCommand, executions);
        AddGitDiffCheck(result.Checks, run, gitOperations);
        AddVerifierResultCheck(result.Checks, run);
        AddChangedFilesCheck(result.Checks, run.ProjectSnapshot, fileOperations, gitOperations);
        AddContextCheck(result.Checks, "Workflow goal available", !string.IsNullOrWhiteSpace(run.GoalText), "Workflow goal is available.", "Workflow goal is missing.");
        AddContextCheck(result.Checks, "Project snapshot available", run.ProjectSnapshot is not null, "Project snapshot is available.", "Project snapshot is missing.");

        if (request.IncludeLocalLlmReview)
        {
            await AddLocalLlmReviewAsync(result, run, executions, fileOperations, gitOperations, dockerOperations, comfyUiOperations, cancellationToken);
        }

        result.Status = DetermineOverallStatus(result.Checks);
        result.Summary = BuildSummary(result);
        result.RecommendedNextAction = BuildRecommendedNextAction(result);
        return await PersistAsync(result, cancellationToken);
    }

    public async Task<IReadOnlyList<VerificationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<VerificationResult?> GetLatestForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        return (await GetForRunAsync(runId, cancellationToken)).FirstOrDefault();
    }

    private static void AddRequiredStepsCheck(List<VerificationCheck> checks, WorkflowRunRecord run)
    {
        var incomplete = run.Steps.Where(step => step.Enabled && step.Status != WorkflowRunStepStatus.Completed).Select(step => step.StepName).ToArray();
        checks.Add(Check(
            "All required steps completed",
            incomplete.Length == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
            incomplete.Length == 0 ? VerificationSeverity.Info : VerificationSeverity.Error,
            incomplete.Length == 0 ? "All enabled workflow steps are completed." : $"Incomplete required steps: {string.Join(", ", incomplete)}."));
    }

    private static void AddFailedOperationsCheck(
        List<VerificationCheck> checks,
        IReadOnlyList<ExecutionResult> executions,
        IReadOnlyList<FileOperationResult> files,
        IReadOnlyList<GitOperationResult> git,
        IReadOnlyList<DockerOperationResult> docker,
        IReadOnlyList<ComfyUiOperationResult> comfyUi)
    {
        var failedExecutions = executions.Count(item => item.Status is ExecutionStatus.Failed or ExecutionStatus.Rejected or ExecutionStatus.TimedOut);
        var failedOperations = files.Count(item => !item.Success) + git.Count(item => !item.Success) + docker.Count(item => !item.Success) + comfyUi.Count(item => !item.Success);
        var total = failedExecutions + failedOperations;
        checks.Add(Check(
            "No failed operations",
            total == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
            total == 0 ? VerificationSeverity.Info : VerificationSeverity.Error,
            total == 0 ? "No failed or rejected operations were recorded." : $"{total} failed, rejected, or timed-out operation(s) were recorded."));
    }

    private static void AddDangerousCommandsCheck(
        List<VerificationCheck> checks,
        IReadOnlyList<ExecutionResult> executions,
        IReadOnlyList<GitOperationResult> git,
        IReadOnlyList<DockerOperationResult> docker)
    {
        var attempted = executions.Select(item => item.CommandText)
            .Concat(git.Select(item => $"{item.BranchName} {item.FilePaths} {item.CommitMessage}"))
            .Concat(docker.Select(item => item.CommandText))
            .SelectMany(command => DangerousCommands.Where(fragment => command.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        checks.Add(Check(
            "No dangerous command attempted",
            attempted.Length == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
            attempted.Length == 0 ? VerificationSeverity.Info : VerificationSeverity.Critical,
            attempted.Length == 0 ? "No dangerous command attempts were detected." : $"Dangerous command fragment(s) detected: {string.Join(", ", attempted)}."));
    }

    private static void AddConfiguredCommandCheck(List<VerificationCheck> checks, string name, string? configuredCommand, IReadOnlyList<ExecutionResult> executions)
    {
        if (string.IsNullOrWhiteSpace(configuredCommand))
        {
            checks.Add(Check(name, VerificationStatus.NotChecked, VerificationSeverity.Info, "The project does not configure this command."));
            return;
        }

        var matching = executions.Where(item => CommandMatches(item.CommandText, configuredCommand)).ToArray();
        var passed = matching.Any(item => item.Status == ExecutionStatus.Completed);
        checks.Add(Check(
            name,
            passed ? VerificationStatus.Passed : VerificationStatus.Failed,
            passed ? VerificationSeverity.Info : VerificationSeverity.Error,
            passed ? $"Configured command completed: {configuredCommand}" : $"Configured command was not completed successfully: {configuredCommand}"));
    }

    private static void AddGitDiffCheck(List<VerificationCheck> checks, WorkflowRunRecord run, IReadOnlyList<GitOperationResult> git)
    {
        var implementationCompletedAt = run.Steps
            .Where(step => step.AgentName.Contains("Implementator", StringComparison.OrdinalIgnoreCase)
                           || step.StepName.Contains("Implementator", StringComparison.OrdinalIgnoreCase))
            .Max(step => step.CompletedAt);
        var diff = git.FirstOrDefault(item => item.OperationType == GitOperationType.Diff
                                              && item.Success
                                              && (implementationCompletedAt is null || item.CreatedAt >= implementationCompletedAt));
        var status = diff is null ? VerificationStatus.Failed : string.IsNullOrWhiteSpace(diff.StandardOutput) ? VerificationStatus.Warning : VerificationStatus.Passed;
        checks.Add(Check(
            "Git diff exists after implementation",
            status,
            status == VerificationStatus.Failed ? VerificationSeverity.Error : status == VerificationStatus.Warning ? VerificationSeverity.Warning : VerificationSeverity.Info,
            diff is null ? "No successful git diff operation was recorded after implementation." : string.IsNullOrWhiteSpace(diff.StandardOutput) ? "Git diff was executed after implementation but returned no changes." : "A successful git diff with changes was recorded after implementation."));
    }

    private static void AddVerifierResultCheck(List<VerificationCheck> checks, WorkflowRunRecord run)
    {
        var verifier = run.Steps.FirstOrDefault(IsVerifierStep);
        var hasResult = verifier is not null && !string.IsNullOrWhiteSpace(verifier.ResultText);
        checks.Add(Check(
            "Verifier step has result",
            hasResult ? VerificationStatus.Passed : VerificationStatus.Failed,
            hasResult ? VerificationSeverity.Info : VerificationSeverity.Error,
            verifier is null ? "No verifier step exists in the workflow run." : hasResult ? "Verifier step contains a result." : "Verifier step result is empty."));
    }

    private static void AddChangedFilesCheck(
        List<VerificationCheck> checks,
        ProjectSnapshot? project,
        IReadOnlyList<FileOperationResult> files,
        IReadOnlyList<GitOperationResult> git)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.LocalPath))
        {
            checks.Add(Check("Changed files are inside project path", VerificationStatus.NotChecked, VerificationSeverity.Warning, "Project local path is unavailable."));
            return;
        }

        var candidates = files
            .Where(item => item.OperationType is FileOperationType.Create or FileOperationType.Write or FileOperationType.Delete or FileOperationType.Rename or FileOperationType.Copy)
            .SelectMany(item => new[] { item.Path, item.TargetPath })
            .Concat(git.Where(item => item.OperationType == GitOperationType.Add).SelectMany(item => SplitPaths(item.FilePaths)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var outside = candidates.Where(path => !IsInsideProject(project.LocalPath, path)).ToArray();
        checks.Add(Check(
            "Changed files are inside project path",
            outside.Length == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
            outside.Length == 0 ? VerificationSeverity.Info : VerificationSeverity.Critical,
            outside.Length == 0 ? $"{candidates.Length} changed file path(s) are inside the project." : $"Changed file path(s) outside project: {string.Join(", ", outside)}."));
    }

    private static void AddContextCheck(List<VerificationCheck> checks, string name, bool available, string success, string failure)
    {
        checks.Add(Check(name, available ? VerificationStatus.Passed : VerificationStatus.Warning, available ? VerificationSeverity.Info : VerificationSeverity.Warning, available ? success : failure));
    }

    private async Task AddLocalLlmReviewAsync(
        VerificationResult result,
        WorkflowRunRecord run,
        IReadOnlyList<ExecutionResult> executions,
        IReadOnlyList<FileOperationResult> files,
        IReadOnlyList<GitOperationResult> git,
        IReadOnlyList<DockerOperationResult> docker,
        IReadOnlyList<ComfyUiOperationResult> comfyUi,
        CancellationToken cancellationToken)
    {
        var verifier = (await agentRegistryService.GetAllAsync(cancellationToken))
            .FirstOrDefault(agent => agent.Enabled && (agent.Role == AgentRole.Verifier || agent.Name.Contains("Verifier", StringComparison.OrdinalIgnoreCase)));
        if (verifier is null)
        {
            result.Checks.Add(Check("Local LLM verifier review", VerificationStatus.NotChecked, VerificationSeverity.Info, "No enabled Verifier agent is configured."));
            return;
        }

        try
        {
            result.LocalLlmReview = await localLlmService.GenerateAsync(verifier.Model, BuildVerifierPrompt(run, result.Checks, executions, files, git, docker, comfyUi), cancellationToken);
            result.Checks.Add(Check("Local LLM verifier review", VerificationStatus.Passed, VerificationSeverity.Info, $"Verifier agent '{verifier.Name}' returned a review."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.Checks.Add(Check("Local LLM verifier review", VerificationStatus.Warning, VerificationSeverity.Warning, $"Verifier agent review failed: {exception.Message}"));
        }
    }

    private static string BuildVerifierPrompt(
        WorkflowRunRecord run,
        IReadOnlyList<VerificationCheck> checks,
        IReadOnlyList<ExecutionResult> executions,
        IReadOnlyList<FileOperationResult> files,
        IReadOnlyList<GitOperationResult> git,
        IReadOnlyList<DockerOperationResult> docker,
        IReadOnlyList<ComfyUiOperationResult> comfyUi)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Review this workflow run verification evidence. Identify remaining risks and give a concise release recommendation.");
        builder.AppendLine($"Goal: {Limit(run.GoalText, 2000)}");
        builder.AppendLine($"Project: {run.ProjectSnapshot?.Name} at {run.ProjectSnapshot?.LocalPath}");
        builder.AppendLine("Deterministic checks:");
        foreach (var check in checks)
        {
            builder.AppendLine($"- {check.Name}: {check.Status}/{check.Severity} - {Limit(check.Message, 500)}");
        }
        builder.AppendLine("Step results:");
        foreach (var step in run.Steps)
        {
            builder.AppendLine($"- {step.StepName} [{step.Status}]: {Limit(step.ResultText, 1200)}");
        }
        builder.AppendLine($"Operation counts: execution={executions.Count}, file={files.Count}, git={git.Count}, docker={docker.Count}, comfyui={comfyUi.Count}");
        builder.AppendLine("Execution results:");
        foreach (var execution in executions.TakeLast(10))
        {
            builder.AppendLine($"- {execution.CommandText} [{execution.Status}] exit={execution.ExitCode}: {Limit(execution.StandardError, 500)}");
        }
        builder.AppendLine("Git diffs:");
        foreach (var diff in git.Where(item => item.OperationType == GitOperationType.Diff).TakeLast(3))
        {
            builder.AppendLine(Limit(diff.StandardOutput, 3000));
        }
        return builder.ToString();
    }

    private static VerificationStatus DetermineOverallStatus(IReadOnlyList<VerificationCheck> checks)
    {
        if (checks.Any(check => check.Status == VerificationStatus.Failed && check.Severity is VerificationSeverity.Error or VerificationSeverity.Critical))
        {
            return VerificationStatus.Failed;
        }
        if (checks.Any(check => check.Status is VerificationStatus.Warning or VerificationStatus.NotChecked or VerificationStatus.Failed))
        {
            return VerificationStatus.Warning;
        }
        return VerificationStatus.Passed;
    }

    private static string BuildSummary(VerificationResult result)
    {
        return $"Verification {result.Status}: {result.Checks.Count(check => check.Status == VerificationStatus.Passed)} passed, "
            + $"{result.Checks.Count(check => check.Status == VerificationStatus.Warning)} warning, "
            + $"{result.Checks.Count(check => check.Status == VerificationStatus.Failed)} failed, "
            + $"{result.Checks.Count(check => check.Status == VerificationStatus.NotChecked)} not checked.";
    }

    private static string BuildRecommendedNextAction(VerificationResult result)
    {
        var critical = result.Checks.FirstOrDefault(check => check.Status == VerificationStatus.Failed && check.Severity == VerificationSeverity.Critical);
        if (critical is not null) return $"Stop and investigate: {critical.Message}";
        var failed = result.Checks.FirstOrDefault(check => check.Status == VerificationStatus.Failed);
        if (failed is not null) return $"Resolve the failed check, then run verification again: {failed.Name}.";
        var warning = result.Checks.FirstOrDefault(check => check.Status is VerificationStatus.Warning or VerificationStatus.NotChecked);
        return warning is not null ? $"Review the warning and rerun verification: {warning.Name}." : "Verification passed. The workflow run is ready for final review.";
    }

    private static bool CommandMatches(string actual, string configured)
    {
        var normalizedActual = NormalizeCommand(actual);
        var normalizedConfigured = NormalizeCommand(configured);
        return normalizedActual.Equals(normalizedConfigured, StringComparison.OrdinalIgnoreCase)
            || normalizedActual.StartsWith(normalizedConfigured + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCommand(string command) => string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static bool IsVerifierStep(WorkflowRunStepRecord step) => step.AgentName.Contains("Verifier", StringComparison.OrdinalIgnoreCase) || step.StepName.Contains("Verifier", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> SplitPaths(string paths) => paths.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsInsideProject(string projectPath, string candidatePath)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var projectFullPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
            var root = projectFullPath + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.IsPathRooted(candidatePath) ? candidatePath : Path.Combine(projectPath, candidatePath));
            return full.Equals(projectFullPath, comparison) || full.StartsWith(root, comparison);
        }
        catch
        {
            return false;
        }
    }

    private static VerificationCheck Check(string name, VerificationStatus status, VerificationSeverity severity, string message) => new()
    {
        Name = name,
        Status = status,
        Severity = severity,
        Message = message
    };

    private static string Limit(string? value, int max) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= max ? value : value[..max] + "...";

    private async Task<VerificationResult> PersistAsync(VerificationResult result, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var results = await LoadAsync(cancellationToken);
            results.Add(Clone(result));
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
            await using var stream = File.Create(ResultsPath);
            await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken);
            return Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<List<VerificationResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ResultsPath)) return [];
        await using var stream = File.OpenRead(ResultsPath);
        return await JsonSerializer.DeserializeAsync<List<VerificationResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static VerificationResult Clone(VerificationResult result) => new()
    {
        Id = result.Id,
        RunId = result.RunId,
        Status = result.Status,
        Checks = result.Checks.Select(check => Check(check.Name, check.Status, check.Severity, check.Message)).ToList(),
        Summary = result.Summary,
        RecommendedNextAction = result.RecommendedNextAction,
        LocalLlmReview = result.LocalLlmReview,
        CreatedAt = result.CreatedAt
    };
}
