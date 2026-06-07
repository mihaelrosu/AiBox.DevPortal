using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class GitOperationService(
    IWorkflowRunHistoryService workflowRunHistoryService,
    IProjectRegistryService projectRegistryService,
    IAgentRegistryService agentRegistryService,
    IExecutionPermissionProfileService executionPermissionProfileService) : IGitOperationService
{
    private const string ResultsPath = "/data/git-operation-results.json";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private static readonly string[] DangerousFragments =
    [
        "git reset --hard",
        "git clean -fd",
        "git push --force",
        "git rebase",
        "git checkout -- .",
        "git restore .",
        "git rm"
    ];

    public async Task<GitOperationResult> ExecuteAsync(GitOperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new GitOperationResult
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = request.RunId?.Trim() ?? string.Empty,
            StepId = request.StepId?.Trim() ?? string.Empty,
            ProjectId = request.ProjectId?.Trim() ?? string.Empty,
            OperationType = request.OperationType,
            BranchName = request.BranchName?.Trim() ?? string.Empty,
            FilePaths = NormalizeFilePaths(request.FilePaths),
            CommitMessage = request.CommitMessage?.Trim() ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            Success = false
        };

        try
        {
            var context = await ResolveContextAsync(result, cancellationToken);
            if (!context.IsValid)
            {
                result.Message = context.ErrorMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!ValidateOperation(result.OperationType, result.BranchName, result.FilePaths, result.CommitMessage, request.Confirmed, context.Profile!, out var validationMessage))
            {
                result.Message = validationMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!ValidateRepository(context.Project!, out var repositoryMessage))
            {
                result.Message = repositoryMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!ValidatePaths(context.Project!.LocalPath, result.OperationType, result.FilePaths, out var pathsMessage))
            {
                result.Message = pathsMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!IsAllowedByProfile(context.Profile!, result, out var profileMessage))
            {
                result.Message = profileMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!request.Confirmed && RequiresConfirmation(result.OperationType))
            {
                result.Message = "This Git operation requires confirmation.";
                return await PersistAsync(result, cancellationToken);
            }

            var execution = await RunGitAsync(context.Project!.LocalPath, result.OperationType, result.BranchName, result.FilePaths, result.CommitMessage, cancellationToken);

            result.StandardOutput = execution.StandardOutput;
            result.StandardError = execution.StandardError;
            result.ExitCode = execution.ExitCode;
            result.Success = execution.ExitCode == 0;
            result.Message = execution.ExitCode == 0 ? "Git operation completed successfully." : "Git operation failed.";

            return await PersistAsync(result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Message = "Operation cancelled.";
            result.StandardError = result.Message;
            return await PersistAsync(result, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Message = exception.Message;
            result.StandardError = exception.Message;
            return await PersistAsync(result, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<GitOperationResult>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken)).OrderByDescending(item => item.CreatedAt).Select(Clone).ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<GitOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var normalized = runId?.Trim() ?? string.Empty;
            return (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<GitOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var results = await LoadAsync(cancellationToken);
            var result = results.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return result is null ? null : Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<GitOperationResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var normalizedRunId = runId?.Trim() ?? string.Empty;
            var normalizedStepId = stepId?.Trim() ?? string.Empty;
            var result = (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(normalizedRunId, StringComparison.OrdinalIgnoreCase) && item.StepId.Equals(normalizedStepId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            return result is null ? null : Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<GitOperationContext> ResolveContextAsync(GitOperationResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.RunId) || string.IsNullOrWhiteSpace(result.StepId))
        {
            return GitOperationContext.Invalid("RunId and StepId are required.");
        }

        var run = await workflowRunHistoryService.GetByIdAsync(result.RunId, cancellationToken);
        if (run is null)
        {
            return GitOperationContext.Invalid($"Workflow run '{result.RunId}' was not found.");
        }

        var step = run.Steps.FirstOrDefault(item => item.StepId.Equals(result.StepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            return GitOperationContext.Invalid($"Workflow step '{result.StepId}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(run.ProjectId))
        {
            return GitOperationContext.Invalid("Workflow run does not have a project assigned.");
        }

        var projectId = string.IsNullOrWhiteSpace(result.ProjectId) ? run.ProjectId : result.ProjectId;
        if (!projectId.Equals(run.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return GitOperationContext.Invalid("Requested project does not match the workflow run project.");
        }

        ProjectDefinition? project = run.ProjectSnapshot is not null
            ? ToProjectDefinition(run.ProjectSnapshot)
            : await projectRegistryService.GetByIdAsync(run.ProjectId, cancellationToken);

        if (project is null)
        {
            return GitOperationContext.Invalid($"Project '{run.ProjectId}' was not found.");
        }

        var agent = await agentRegistryService.GetByIdAsync(step.AgentId, cancellationToken);
        if (agent is null)
        {
            return GitOperationContext.Invalid($"Agent '{step.AgentId}' was not found.");
        }

        var profile = !string.IsNullOrWhiteSpace(agent.ExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(agent.ExecutionPermissionProfileId, cancellationToken)
            : null;

        profile ??= !string.IsNullOrWhiteSpace(project.DefaultExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(project.DefaultExecutionPermissionProfileId, cancellationToken)
            : null;

        if (profile is null)
        {
            return GitOperationContext.Invalid($"No execution permission profile is configured for agent '{agent.Name}'.");
        }

        return GitOperationContext.Valid(run, step, project, profile);
    }

    private static bool ValidateRepository(ProjectDefinition project, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(project.LocalPath))
        {
            message = "Project local path is required.";
            return false;
        }

        if (!Directory.Exists(project.LocalPath))
        {
            message = $"Project local path '{project.LocalPath}' was not found.";
            return false;
        }

        var gitFolder = Path.Combine(project.LocalPath, ".git");
        if (string.IsNullOrWhiteSpace(project.GitRepository) && !Directory.Exists(gitFolder))
        {
            message = "Project must have a Git repository configured or a .git folder present.";
            return false;
        }

        return true;
    }

    private static bool ValidateOperation(GitOperationType operationType, string branchName, string filePaths, string commitMessage, bool confirmed, ExecutionPermissionProfile profile, out string message)
    {
        message = string.Empty;

        if (operationType is GitOperationType.CreateBranch or GitOperationType.CheckoutBranch && string.IsNullOrWhiteSpace(branchName))
        {
            message = "BranchName is required for branch operations.";
            return false;
        }

        if (operationType == GitOperationType.Add && string.IsNullOrWhiteSpace(filePaths))
        {
            message = "FilePaths are required for git add.";
            return false;
        }

        if (operationType == GitOperationType.Commit && string.IsNullOrWhiteSpace(commitMessage))
        {
            message = "CommitMessage is required for git commit.";
            return false;
        }

        if (RequiresConfirmation(operationType) && !confirmed)
        {
            message = "This Git operation requires confirmation.";
            return false;
        }

        if (profile.RequiresConfirmation && operationType is not GitOperationType.Status and not GitOperationType.Diff and not GitOperationType.Log and not GitOperationType.Branch)
        {
            if (!confirmed)
            {
                message = "This Git operation requires confirmation by the selected execution profile.";
                return false;
            }
        }

        if (branchName.StartsWith("-", StringComparison.Ordinal))
        {
            message = "Branch names cannot start with '-'";
            return false;
        }

        foreach (var fragment in DangerousFragments)
        {
            if (branchName.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || filePaths.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || commitMessage.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                message = $"Command fragment '{fragment}' is not allowed.";
                return false;
            }
        }

        return true;
    }

    private static bool ValidatePaths(string projectLocalPath, GitOperationType operationType, string filePaths, out string message)
    {
        message = string.Empty;

        if (operationType != GitOperationType.Add)
        {
            return true;
        }

        var paths = SplitFilePaths(filePaths);
        if (paths.Count == 0)
        {
            message = "At least one file path is required.";
            return false;
        }

        foreach (var path in paths)
        {
            if (!IsInsideProject(projectLocalPath, path))
            {
                message = $"Path '{path}' is outside the project local path.";
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedByProfile(ExecutionPermissionProfile profile, GitOperationResult request, out string message)
    {
        message = string.Empty;

        if (!profile.AllowRunGit)
        {
            message = $"Git operations are not permitted by profile '{profile.Name}'.";
            return false;
        }

        var commandText = BuildCommandText(request);

        if (profile.BlockedCommands.Any(blocked => commandText.Contains(blocked, StringComparison.OrdinalIgnoreCase)))
        {
            message = "The requested Git command is blocked by the execution profile.";
            return false;
        }

        if (profile.AllowedCommands.Count > 0 && !profile.AllowedCommands.Any(allowed => commandText.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || commandText.Equals(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            message = "The requested Git command is not in the profile's allowed command list.";
            return false;
        }

        return true;
    }

    private static bool RequiresConfirmation(GitOperationType operationType)
    {
        return operationType is GitOperationType.CheckoutBranch or GitOperationType.Add or GitOperationType.Commit;
    }

    private static string BuildCommandText(GitOperationResult request)
    {
        return request.OperationType switch
        {
            GitOperationType.Status => "git status --short",
            GitOperationType.Diff => "git diff",
            GitOperationType.Log => "git log --oneline -20",
            GitOperationType.Branch => "git branch --show-current",
            GitOperationType.CreateBranch => $"git checkout -b {request.BranchName}",
            GitOperationType.CheckoutBranch => $"git checkout {request.BranchName}",
            GitOperationType.Add => $"git add -- {request.FilePaths}",
            GitOperationType.Commit => $"git commit -m {request.CommitMessage}",
            _ => "git"
        };
    }

    private static string NormalizeFilePaths(string filePaths)
    {
        return string.Join(Environment.NewLine, SplitFilePaths(filePaths));
    }

    private static IReadOnlyList<string> SplitFilePaths(string filePaths)
    {
        if (string.IsNullOrWhiteSpace(filePaths))
        {
            return [];
        }

        return filePaths
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsInsideProject(string projectLocalPath, string candidatePath)
    {
        var projectRoot = Path.GetFullPath(projectLocalPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(Path.IsPathRooted(candidatePath) ? candidatePath : Path.Combine(projectLocalPath, candidatePath));
        return candidateFull.Equals(Path.GetFullPath(projectLocalPath), StringComparison.OrdinalIgnoreCase)
            || candidateFull.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GitProcessResult> RunGitAsync(string workingDirectory, GitOperationType operationType, string branchName, string filePaths, string commitMessage, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in BuildArguments(operationType, branchName, filePaths, commitMessage))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardOutput.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardError.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort only.
            }

            return new GitProcessResult(-1, standardOutput.ToString(), standardError.ToString() + Environment.NewLine + "Git operation timed out.");
        }

        await process.WaitForExitAsync(cancellationToken);

        return new GitProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static IReadOnlyList<string> BuildArguments(GitOperationType operationType, string branchName, string filePaths, string commitMessage)
    {
        var args = new List<string>();

        switch (operationType)
        {
            case GitOperationType.Status:
                args.AddRange(["status", "--short"]);
                break;
            case GitOperationType.Diff:
                args.Add("diff");
                break;
            case GitOperationType.Log:
                args.AddRange(["log", "--oneline", "-20"]);
                break;
            case GitOperationType.Branch:
                args.AddRange(["branch", "--show-current"]);
                break;
            case GitOperationType.CreateBranch:
                args.AddRange(["checkout", "-b", branchName]);
                break;
            case GitOperationType.CheckoutBranch:
                args.AddRange(["checkout", branchName]);
                break;
            case GitOperationType.Add:
                args.Add("add");
                args.Add("--");
                args.AddRange(SplitFilePaths(filePaths));
                break;
            case GitOperationType.Commit:
                args.AddRange(["commit", "-m", commitMessage]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationType), operationType, "Unsupported Git operation.");
        }

        return args;
    }

    private async Task<GitOperationResult> PersistAsync(GitOperationResult result, CancellationToken cancellationToken)
    {
        result.CreatedAt = DateTimeOffset.UtcNow;

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var results = await LoadAsync(cancellationToken);
            var index = results.FindIndex(item => item.Id.Equals(result.Id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                results.Add(Clone(result));
            }
            else
            {
                results[index] = Clone(result);
            }

            await SaveAsync(results, cancellationToken);
            return Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<List<GitOperationResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ResultsPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(ResultsPath);
        return await JsonSerializer.DeserializeAsync<List<GitOperationResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<GitOperationResult> results, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
        await using var stream = File.Create(ResultsPath);
        await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken);
    }

    private static GitOperationResult Clone(GitOperationResult result)
    {
        return new GitOperationResult
        {
            Id = result.Id,
            RunId = result.RunId,
            StepId = result.StepId,
            ProjectId = result.ProjectId,
            OperationType = result.OperationType,
            BranchName = result.BranchName,
            FilePaths = result.FilePaths,
            CommitMessage = result.CommitMessage,
            Success = result.Success,
            Message = result.Message,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            ExitCode = result.ExitCode,
            CreatedAt = result.CreatedAt
        };
    }

    private static ProjectDefinition ToProjectDefinition(ProjectSnapshot snapshot)
    {
        return new ProjectDefinition
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            Type = snapshot.Type,
            Description = snapshot.Description,
            LocalPath = snapshot.LocalPath,
            GitRepository = snapshot.GitRepository,
            DefaultBranch = snapshot.DefaultBranch,
            BuildCommand = snapshot.BuildCommand,
            RunCommand = snapshot.RunCommand,
            TestCommand = snapshot.TestCommand,
            DefaultExecutionPermissionProfileId = snapshot.DefaultExecutionPermissionProfileId,
            Enabled = snapshot.Enabled
        };
    }

    private sealed record GitOperationContext(bool IsValid, string ErrorMessage, WorkflowRunRecord? Run, WorkflowRunStepRecord? Step, ProjectDefinition? Project, ExecutionPermissionProfile? Profile)
    {
        public static GitOperationContext Invalid(string errorMessage) => new(false, errorMessage, null, null, null, null);
        public static GitOperationContext Valid(WorkflowRunRecord run, WorkflowRunStepRecord step, ProjectDefinition project, ExecutionPermissionProfile profile) => new(true, string.Empty, run, step, project, profile);
    }

    private sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
