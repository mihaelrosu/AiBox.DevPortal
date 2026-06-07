using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class ExecutionEngineService(
    IWorkflowRunHistoryService workflowRunHistoryService,
    IProjectRegistryService projectRegistryService,
    IAgentRegistryService agentRegistryService,
    IExecutionPermissionProfileService executionPermissionProfileService) : IExecutionEngineService
{
    private const string ResultsPath = "/data/execution-results.json";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private static readonly string[] DangerousFragments =
    [
        "rm -rf /",
        "mkfs",
        "dd if=",
        "shutdown",
        "reboot",
        "docker system prune",
        "docker volume prune",
        "docker volume rm",
        "docker rm -f"
    ];

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var result = new ExecutionResult
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = request.RunId?.Trim() ?? string.Empty,
            StepId = request.StepId?.Trim() ?? string.Empty,
            StartedAt = now,
            CompletedAt = now,
            Status = ExecutionStatus.Rejected,
            WorkingDirectory = request.WorkingDirectory?.Trim() ?? string.Empty,
            CommandType = request.CommandType,
            CommandText = BuildCommandText(request)
        };

        if (string.IsNullOrWhiteSpace(result.RunId) || string.IsNullOrWhiteSpace(result.StepId))
        {
            result.StandardError = "RunId and StepId are required.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        var run = await workflowRunHistoryService.GetByIdAsync(result.RunId, cancellationToken);

        if (run is null)
        {
            result.StandardError = $"Workflow run '{result.RunId}' was not found.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        var step = run.Steps.FirstOrDefault(step => step.StepId.Equals(result.StepId, StringComparison.OrdinalIgnoreCase));

        if (step is null)
        {
            result.StandardError = $"Workflow step '{result.StepId}' was not found.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(run.ProjectId))
        {
            result.StandardError = "Workflow run does not have a project assigned.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId) && !request.ProjectId.Equals(run.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            result.StandardError = "Requested project does not match the workflow run project.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        var project = run.ProjectSnapshot is not null
            ? ToProjectDefinition(run.ProjectSnapshot)
            : await projectRegistryService.GetByIdAsync(run.ProjectId, cancellationToken);

        if (project is null)
        {
            result.StandardError = $"Project '{run.ProjectId}' was not found.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId) && !request.ProjectId.Equals(project.Id, StringComparison.OrdinalIgnoreCase))
        {
            result.StandardError = "Requested project does not match the resolved project.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        var agent = await agentRegistryService.GetByIdAsync(step.AgentId, cancellationToken);

        if (agent is null)
        {
            result.StandardError = $"Agent '{step.AgentId}' was not found.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        var profile = !string.IsNullOrWhiteSpace(agent.ExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(agent.ExecutionPermissionProfileId, cancellationToken)
            : null;

        profile ??= !string.IsNullOrWhiteSpace(project.DefaultExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(project.DefaultExecutionPermissionProfileId, cancellationToken)
            : null;

        if (profile is null)
        {
            result.StandardError = $"No execution permission profile is configured for agent '{agent.Name}'.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        result.WorkingDirectory = ResolveWorkingDirectory(request.WorkingDirectory ?? string.Empty, project.LocalPath);
        result.CommandText = BuildCommandText(request);

        var preflightError = ValidatePreflight(profile, project, request, result.WorkingDirectory, result.CommandText);

        if (preflightError is not null)
        {
            result.StandardError = preflightError;
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        if ((request.RequiresConfirmation || profile.RequiresConfirmation || profile.AllowDeleteFiles) && !request.Confirmed)
        {
            result.Status = ExecutionStatus.WaitingForConfirmation;
            result.StandardError = "Execution requires confirmation before the command can be run.";
            return await PersistAndAnnotateAsync(result, cancellationToken);
        }

        result.Status = ExecutionStatus.Running;
        result.StartedAt = DateTimeOffset.UtcNow;
        result.CompletedAt = result.StartedAt;

        try
        {
            var processResult = await RunProcessAsync(request, result.WorkingDirectory, result.CommandText, cancellationToken);

            result.CompletedAt = DateTimeOffset.UtcNow;
            result.ExitCode = processResult.ExitCode;
            result.StandardOutput = processResult.StandardOutput;
            result.StandardError = processResult.StandardError;
            result.Status = processResult.TimedOut
                ? ExecutionStatus.TimedOut
                : processResult.ExitCode == 0
                    ? ExecutionStatus.Completed
                    : ExecutionStatus.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.CompletedAt = DateTimeOffset.UtcNow;
            result.StandardError = "Execution cancelled.";
            result.Status = ExecutionStatus.Failed;
            throw;
        }
        catch (Exception exception)
        {
            result.CompletedAt = DateTimeOffset.UtcNow;
            result.StandardError = exception.Message;
            result.Status = ExecutionStatus.Failed;
        }

        await PersistAndAnnotateAsync(result, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<ExecutionResult>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(result => result.CompletedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ExecutionResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var results = await LoadAsync(cancellationToken);
            var result = results.FirstOrDefault(result => result.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return result is null ? null : Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ExecutionResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var results = await LoadAsync(cancellationToken);
            var result = results
                .Where(result => result.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase)
                                 && result.StepId.Equals(stepId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(result => result.CompletedAt)
                .FirstOrDefault();

            return result is null ? null : Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<ExecutionResult> PersistAndAnnotateAsync(ExecutionResult result, CancellationToken cancellationToken)
    {
        await SaveResultAsync(result, cancellationToken);
        await AppendStepNotesAsync(result, cancellationToken);
        return result;
    }

    private async Task AppendStepNotesAsync(ExecutionResult result, CancellationToken cancellationToken)
    {
        var notes = BuildExecutionSummary(result);

        try
        {
            await workflowRunHistoryService.AppendStepNotesAsync(result.RunId, result.StepId, notes, cancellationToken);
        }
        catch
        {
            // The execution record is already persisted; note updates are best-effort.
        }
    }

    private async Task SaveResultAsync(ExecutionResult result, CancellationToken cancellationToken)
    {
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
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<ProcessRunResult> RunProcessAsync(
        ExecutionRequest request,
        string workingDirectory,
        string commandText,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(request.CommandType, request.Command, request.Arguments, workingDirectory, commandText),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("The command process could not be started.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(DefaultTimeout);

        var completedTask = await Task.WhenAny(exitTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            TryKill(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessRunResult
            {
                ExitCode = null,
                StandardOutput = await stdoutTask,
                StandardError = $"{await stderrTask}{Environment.NewLine}Execution timed out after {DefaultTimeout.TotalSeconds:0} seconds.".Trim(),
                TimedOut = true
            };
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        return new ProcessRunResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdoutTask,
            StandardError = await stderrTask,
            TimedOut = false
        };
    }

    private static ProcessStartInfo CreateStartInfo(
        ExecutionCommandType commandType,
        string command,
        string arguments,
        string workingDirectory,
        string commandText)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        switch (commandType)
        {
            case ExecutionCommandType.Shell:
                startInfo.FileName = "/bin/bash";
                startInfo.ArgumentList.Add("-lc");
                startInfo.ArgumentList.Add(commandText);
                break;
            case ExecutionCommandType.DotNet:
                startInfo.FileName = "dotnet";
                AddArgumentTokens(startInfo, command);
                AddArgumentTokens(startInfo, arguments);
                break;
            case ExecutionCommandType.Git:
                startInfo.FileName = "git";
                AddArgumentTokens(startInfo, command);
                AddArgumentTokens(startInfo, arguments);
                break;
            case ExecutionCommandType.Docker:
                startInfo.FileName = "docker";
                AddArgumentTokens(startInfo, command);
                AddArgumentTokens(startInfo, arguments);
                break;
            case ExecutionCommandType.Python:
                startInfo.FileName = "python3";
                AddArgumentTokens(startInfo, command);
                AddArgumentTokens(startInfo, arguments);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(commandType), commandType, "Unsupported execution command type.");
        }

        return startInfo;
    }

    private static void AddArgumentTokens(ProcessStartInfo startInfo, string? arguments)
    {
        foreach (var token in Tokenize(arguments))
        {
            startInfo.ArgumentList.Add(token);
        }
    }

    private static IEnumerable<string> Tokenize(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        var builder = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        foreach (var character in arguments.Trim())
        {
            if (character is '\'' or '\"')
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quoteChar = character;
                    continue;
                }

                if (quoteChar == character)
                {
                    inQuotes = false;
                    quoteChar = '\0';
                    continue;
                }
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            builder.Append(character);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string ValidatePreflight(
        ExecutionPermissionProfile profile,
        ProjectDefinition project,
        ExecutionRequest request,
        string workingDirectory,
        string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return "A command is required before execution can start.";
        }

        if (!IsCommandTypeAllowed(profile, request.CommandType))
        {
            return $"Execution type '{request.CommandType}' is not allowed by profile '{profile.Name}'.";
        }

        if (!Directory.Exists(workingDirectory))
        {
            return $"Working directory '{workingDirectory}' does not exist.";
        }

        if (IsBlockedCommand(commandText, profile))
        {
            return $"Command '{commandText}' is blocked by the execution safety rules.";
        }

        if (!IsAllowedCommand(commandText, profile))
        {
            return $"Command '{commandText}' is not allowed by profile '{profile.Name}'.";
        }

        if (!IsWorkingDirectoryAllowed(profile, project, workingDirectory))
        {
            return $"Working directory '{workingDirectory}' is not allowed by profile '{profile.Name}'.";
        }

        return string.Empty;
    }

    private static bool IsCommandTypeAllowed(ExecutionPermissionProfile profile, ExecutionCommandType commandType)
    {
        return commandType switch
        {
            ExecutionCommandType.Shell => profile.AllowRunShell,
            ExecutionCommandType.DotNet => profile.AllowRunDotNet,
            ExecutionCommandType.Git => profile.AllowRunGit,
            ExecutionCommandType.Docker => profile.AllowRunDocker,
            ExecutionCommandType.Python => profile.AllowRunPython,
            _ => false
        };
    }

    private static bool IsAllowedCommand(string commandText, ExecutionPermissionProfile profile)
    {
        if (profile.AllowedCommands.Count == 0)
        {
            return true;
        }

        return profile.AllowedCommands.Any(allowed =>
            commandText.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || commandText.StartsWith($"{allowed} ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlockedCommand(string commandText, ExecutionPermissionProfile profile)
    {
        if (DangerousFragments.Any(fragment => commandText.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return profile.BlockedCommands.Any(blocked =>
            commandText.Equals(blocked, StringComparison.OrdinalIgnoreCase)
            || commandText.Contains(blocked, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWorkingDirectoryAllowed(
        ExecutionPermissionProfile profile,
        ProjectDefinition project,
        string workingDirectory)
    {
        var allowedDirectories = profile.AllowedWorkingDirectories.Count > 0
            ? profile.AllowedWorkingDirectories
            : profile.Level == ExecutionPermissionLevel.FullLocalAdmin
                ? ["/"]
                : [project.LocalPath];

        var blockedDirectories = profile.BlockedWorkingDirectories;

        if (blockedDirectories.Any(blocked => IsPathInside(blocked, workingDirectory)))
        {
            return false;
        }

        return allowedDirectories.Any(allowed => IsPathInside(allowed, workingDirectory));
    }

    private static string ResolveWorkingDirectory(string requestedWorkingDirectory, string projectPath)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedWorkingDirectory)
            ? projectPath
            : Path.IsPathRooted(requestedWorkingDirectory)
                ? requestedWorkingDirectory
                : Path.Combine(projectPath, requestedWorkingDirectory);

        return Path.GetFullPath(candidate);
    }

    private static bool IsPathInside(string parentPath, string candidatePath)
    {
        var parentFullPath = Path.GetFullPath(parentPath);
        var candidateFullPath = Path.GetFullPath(candidatePath);
        var normalizedParent = parentFullPath == Path.GetPathRoot(parentFullPath)
            ? parentFullPath
            : parentFullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return candidateFullPath.Equals(parentFullPath, StringComparison.OrdinalIgnoreCase)
            || candidateFullPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCommandText(ExecutionRequest request)
    {
        var command = request.Command?.Trim() ?? string.Empty;
        var arguments = request.Arguments?.Trim() ?? string.Empty;

        return request.CommandType switch
        {
            ExecutionCommandType.Shell => CombineShellCommand(command, arguments),
            ExecutionCommandType.DotNet => CombineCommand("dotnet", command, arguments),
            ExecutionCommandType.Git => CombineCommand("git", command, arguments),
            ExecutionCommandType.Docker => CombineCommand("docker", command, arguments),
            ExecutionCommandType.Python => CombineCommand("python3", command, arguments),
            _ => CombineShellCommand(command, arguments)
        };
    }

    private static string CombineCommand(string prefix, string command, string arguments)
    {
        var builder = new StringBuilder(prefix);

        if (!string.IsNullOrWhiteSpace(command))
        {
            builder.Append(' ').Append(command);
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            builder.Append(' ').Append(arguments);
        }

        return builder.ToString().Trim();
    }

    private static string CombineShellCommand(string command, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return command;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return arguments;
        }

        return $"{command} {arguments}".Trim();
    }

    private static string BuildExecutionSummary(ExecutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Execution]");
        builder.AppendLine($"Result ID: {result.Id}");
        builder.AppendLine($"Status: {result.Status}");
        builder.AppendLine($"Command: {result.CommandText}");
        builder.AppendLine($"Working directory: {result.WorkingDirectory}");

        if (result.ExitCode is not null)
        {
            builder.AppendLine($"Exit code: {result.ExitCode}");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine("Stdout captured in execution results.");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine("Stderr captured in execution results.");
        }

        return builder.ToString().Trim();
    }

    private static void TryKill(Process process)
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

    private static async Task<List<ExecutionResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ResultsPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(ResultsPath);
        return await JsonSerializer.DeserializeAsync<List<ExecutionResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<ExecutionResult> results, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);

        await using var stream = File.Create(ResultsPath);
        await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken);
    }

    private static ExecutionResult Clone(ExecutionResult result)
    {
        return new ExecutionResult
        {
            Id = result.Id,
            RunId = result.RunId,
            StepId = result.StepId,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            Status = result.Status,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            WorkingDirectory = result.WorkingDirectory,
            CommandType = result.CommandType,
            CommandText = result.CommandText
        };
    }

    private sealed record ProcessRunResult
    {
        public int? ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public bool TimedOut { get; init; }
    }

}
