using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class DockerOperationService(
    IWorkflowRunHistoryService workflowRunHistoryService,
    IProjectRegistryService projectRegistryService,
    IAgentRegistryService agentRegistryService,
    IExecutionPermissionProfileService executionPermissionProfileService) : IDockerOperationService
{
    private const string ResultsPath = "/data/docker-operation-results.json";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string[] DangerousFragments =
    [
        "docker system prune",
        "docker volume prune",
        "docker volume rm",
        "docker rm -f",
        "docker rmi",
        "docker compose down -v",
        "docker compose rm"
    ];

    public async Task<DockerOperationResult> ExecuteAsync(DockerOperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new DockerOperationResult
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = request.RunId?.Trim() ?? string.Empty,
            StepId = request.StepId?.Trim() ?? string.Empty,
            ProjectId = request.ProjectId?.Trim() ?? string.Empty,
            OperationType = request.OperationType,
            ComposeFilePath = request.ComposeFilePath?.Trim() ?? string.Empty,
            ServiceName = request.ServiceName?.Trim() ?? string.Empty,
            Lines = request.Lines,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            var context = await ResolveContextAsync(result, cancellationToken);
            if (!context.IsValid)
            {
                result.Message = context.ErrorMessage;
                return await PersistAsync(result, cancellationToken);
            }

            result.ProjectId = context.Project!.Id;
            result.WorkingDirectory = context.Project.LocalPath;

            if (!Validate(result, request.Confirmed, context.Profile!, out var message))
            {
                result.Message = message;
                return await PersistAsync(result, cancellationToken);
            }

            result.ComposeFilePath = ResolveComposeFilePath(result.ComposeFilePath, context.Project.LocalPath);
            result.CommandText = BuildCommandText(result);

            var execution = await RunDockerAsync(result, cancellationToken);
            result.StandardOutput = execution.StandardOutput;
            result.StandardError = execution.StandardError;
            result.ExitCode = execution.ExitCode;
            result.Success = execution.ExitCode == 0;
            result.Message = result.Success ? "Docker operation completed successfully." : "Docker operation failed.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Message = "Docker operation cancelled.";
            result.StandardError = result.Message;
        }
        catch (Exception exception)
        {
            result.Message = exception.Message;
            result.StandardError = exception.Message;
        }

        return await PersistAsync(result, cancellationToken);
    }

    public async Task<IReadOnlyList<DockerOperationResult>> GetAllAsync(CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<DockerOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
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

    public async Task<DockerOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var result = (await LoadAsync(cancellationToken)).FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return result is null ? null : Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<DockerOperationResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var result = (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase)
                               && item.StepId.Equals(stepId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            return result is null ? null : Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<DockerOperationContext> ResolveContextAsync(DockerOperationResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.RunId) || string.IsNullOrWhiteSpace(result.StepId))
        {
            return DockerOperationContext.Invalid("RunId and StepId are required.");
        }

        var run = await workflowRunHistoryService.GetByIdAsync(result.RunId, cancellationToken);
        if (run is null)
        {
            return DockerOperationContext.Invalid($"Workflow run '{result.RunId}' was not found.");
        }

        var step = run.Steps.FirstOrDefault(item => item.StepId.Equals(result.StepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            return DockerOperationContext.Invalid($"Workflow step '{result.StepId}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(run.ProjectId))
        {
            return DockerOperationContext.Invalid("Workflow run does not have a project assigned.");
        }

        if (!string.IsNullOrWhiteSpace(result.ProjectId) && !result.ProjectId.Equals(run.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return DockerOperationContext.Invalid("Requested project does not match the workflow run project.");
        }

        var project = run.ProjectSnapshot is not null
            ? ToProjectDefinition(run.ProjectSnapshot)
            : await projectRegistryService.GetByIdAsync(run.ProjectId, cancellationToken);
        if (project is null)
        {
            return DockerOperationContext.Invalid($"Project '{run.ProjectId}' was not found.");
        }

        var agent = await agentRegistryService.GetByIdAsync(step.AgentId, cancellationToken);
        if (agent is null)
        {
            return DockerOperationContext.Invalid($"Agent '{step.AgentId}' was not found.");
        }

        var profile = !string.IsNullOrWhiteSpace(agent.ExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(agent.ExecutionPermissionProfileId, cancellationToken)
            : null;
        profile ??= !string.IsNullOrWhiteSpace(project.DefaultExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(project.DefaultExecutionPermissionProfileId, cancellationToken)
            : null;

        return profile is null
            ? DockerOperationContext.Invalid($"No execution permission profile is configured for agent '{agent.Name}'.")
            : DockerOperationContext.Valid(project, profile);
    }

    private static bool Validate(DockerOperationResult result, bool confirmed, ExecutionPermissionProfile profile, out string message)
    {
        message = string.Empty;

        if (!profile.Enabled || !profile.AllowRunDocker)
        {
            message = $"Docker operations are not permitted by profile '{profile.Name}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.WorkingDirectory) || !Directory.Exists(result.WorkingDirectory))
        {
            message = $"Project local path '{result.WorkingDirectory}' was not found.";
            return false;
        }

        if (result.OperationType is DockerOperationType.ComposePs or DockerOperationType.ComposeUp or DockerOperationType.ComposeDown or DockerOperationType.ComposeRestart)
        {
            if (string.IsNullOrWhiteSpace(result.ComposeFilePath))
            {
                message = "ComposeFilePath is required for Docker Compose operations.";
                return false;
            }

            var composePath = ResolveComposeFilePath(result.ComposeFilePath, result.WorkingDirectory);
            if (!IsInsideProject(result.WorkingDirectory, composePath))
            {
                message = "ComposeFilePath must be inside the project local path.";
                return false;
            }

            if (!File.Exists(composePath))
            {
                message = $"Compose file '{composePath}' was not found.";
                return false;
            }
        }

        if (result.OperationType is DockerOperationType.Logs or DockerOperationType.ComposeRestart
            && string.IsNullOrWhiteSpace(result.ServiceName))
        {
            message = "ServiceName is required for logs and compose restart operations.";
            return false;
        }

        if (result.ServiceName.StartsWith("-", StringComparison.Ordinal))
        {
            message = "ServiceName cannot start with '-'.";
            return false;
        }

        if (result.OperationType == DockerOperationType.Logs && result.Lines is < 1 or > 10000)
        {
            message = "Lines must be between 1 and 10000.";
            return false;
        }

        if (RequiresConfirmation(result.OperationType) && !confirmed)
        {
            message = "This Docker operation requires confirmation.";
            return false;
        }

        var commandText = BuildCommandText(result);
        if (DangerousFragments.Any(fragment => commandText.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            || profile.BlockedCommands.Any(blocked => commandText.Contains(blocked, StringComparison.OrdinalIgnoreCase)))
        {
            message = "The requested Docker command is blocked.";
            return false;
        }

        return true;
    }

    private static bool RequiresConfirmation(DockerOperationType operationType)
    {
        return operationType is DockerOperationType.ComposeUp or DockerOperationType.ComposeDown or DockerOperationType.ComposeRestart;
    }

    private static string ResolveComposeFilePath(string composeFilePath, string projectLocalPath)
    {
        return Path.GetFullPath(Path.IsPathRooted(composeFilePath)
            ? composeFilePath
            : Path.Combine(projectLocalPath, composeFilePath));
    }

    private static bool IsInsideProject(string projectLocalPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var projectRoot = Path.GetFullPath(projectLocalPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidatePath);
        return candidateFull.StartsWith(projectRoot, comparison);
    }

    private static string BuildCommandText(DockerOperationResult result)
    {
        return result.OperationType switch
        {
            DockerOperationType.Ps => "docker ps",
            DockerOperationType.Images => "docker images",
            DockerOperationType.Logs => $"docker logs --tail {result.Lines} {result.ServiceName}",
            DockerOperationType.ComposePs => $"docker compose -f {result.ComposeFilePath} ps",
            DockerOperationType.ComposeUp => $"docker compose -f {result.ComposeFilePath} up -d",
            DockerOperationType.ComposeDown => $"docker compose -f {result.ComposeFilePath} down",
            DockerOperationType.ComposeRestart => $"docker compose -f {result.ComposeFilePath} restart {result.ServiceName}",
            _ => throw new ArgumentOutOfRangeException(nameof(result.OperationType), result.OperationType, "Unsupported Docker operation.")
        };
    }

    private static async Task<DockerProcessResult> RunDockerAsync(DockerOperationResult result, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = result.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in BuildArguments(result))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) standardOutput.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) standardError.AppendLine(args.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start docker process.");
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
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only.
            }

            return new DockerProcessResult(-1, standardOutput.ToString(), standardError + Environment.NewLine + "Docker operation timed out.");
        }

        return new DockerProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static IReadOnlyList<string> BuildArguments(DockerOperationResult result)
    {
        return result.OperationType switch
        {
            DockerOperationType.Ps => ["ps"],
            DockerOperationType.Images => ["images"],
            DockerOperationType.Logs => ["logs", "--tail", result.Lines.ToString(), result.ServiceName],
            DockerOperationType.ComposePs => ["compose", "-f", result.ComposeFilePath, "ps"],
            DockerOperationType.ComposeUp => ["compose", "-f", result.ComposeFilePath, "up", "-d"],
            DockerOperationType.ComposeDown => ["compose", "-f", result.ComposeFilePath, "down"],
            DockerOperationType.ComposeRestart => ["compose", "-f", result.ComposeFilePath, "restart", result.ServiceName],
            _ => throw new ArgumentOutOfRangeException(nameof(result.OperationType), result.OperationType, "Unsupported Docker operation.")
        };
    }

    private async Task<DockerOperationResult> PersistAsync(DockerOperationResult result, CancellationToken cancellationToken)
    {
        result.CreatedAt = DateTimeOffset.UtcNow;
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var results = await LoadAsync(cancellationToken);
            results.Add(Clone(result));
            await SaveAsync(results, cancellationToken);
            return Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<List<DockerOperationResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ResultsPath)) return [];
        await using var stream = File.OpenRead(ResultsPath);
        return await JsonSerializer.DeserializeAsync<List<DockerOperationResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<DockerOperationResult> results, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
        await using var stream = File.Create(ResultsPath);
        await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken);
    }

    private static DockerOperationResult Clone(DockerOperationResult result)
    {
        return new DockerOperationResult
        {
            Id = result.Id,
            RunId = result.RunId,
            StepId = result.StepId,
            ProjectId = result.ProjectId,
            OperationType = result.OperationType,
            ComposeFilePath = result.ComposeFilePath,
            ServiceName = result.ServiceName,
            Lines = result.Lines,
            WorkingDirectory = result.WorkingDirectory,
            CommandText = result.CommandText,
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

    private sealed record DockerOperationContext(bool IsValid, string ErrorMessage, ProjectDefinition? Project, ExecutionPermissionProfile? Profile)
    {
        public static DockerOperationContext Invalid(string message) => new(false, message, null, null);
        public static DockerOperationContext Valid(ProjectDefinition project, ExecutionPermissionProfile profile) => new(true, string.Empty, project, profile);
    }

    private sealed record DockerProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
