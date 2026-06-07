using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class ComfyUiOperationService(
    IComfyUiService comfyUiService,
    IWorkflowRunHistoryService workflowRunHistoryService,
    IProjectRegistryService projectRegistryService,
    IAgentRegistryService agentRegistryService,
    IExecutionPermissionProfileService executionPermissionProfileService) : IComfyUiOperationService
{
    private const string ResultsPath = "/data/comfyui-operation-results.json";
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<ComfyUiOperationResult> ExecuteAsync(ComfyUiOperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new ComfyUiOperationResult
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = request.RunId?.Trim() ?? string.Empty,
            StepId = request.StepId?.Trim() ?? string.Empty,
            ProjectId = request.ProjectId?.Trim() ?? string.Empty,
            OperationType = request.OperationType,
            WorkflowFileName = request.WorkflowFileName?.Trim() ?? string.Empty,
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

            if (!ValidateRequest(request, context.Profile!, out var validationMessage))
            {
                result.Message = validationMessage;
                return await PersistAsync(result, cancellationToken);
            }

            await ExecuteOperationAsync(request, result, cancellationToken);
            if (request.OperationType != ComfyUiOperationType.Health)
            {
                result.Success = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Message = "ComfyUI operation cancelled.";
        }
        catch (Exception exception)
        {
            result.Message = exception.Message;
        }

        return await PersistAsync(result, cancellationToken);
    }

    public async Task<IReadOnlyList<ComfyUiOperationResult>> GetAllAsync(CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<ComfyUiOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
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

    public async Task<ComfyUiOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
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

    public async Task<ComfyUiOperationResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default)
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

    private async Task ExecuteOperationAsync(ComfyUiOperationRequest request, ComfyUiOperationResult result, CancellationToken cancellationToken)
    {
        switch (request.OperationType)
        {
            case ComfyUiOperationType.Health:
                var health = await comfyUiService.GetHealthAsync(cancellationToken);
                result.ResultJson = JsonSerializer.Serialize(health, JsonOptions);
                result.Success = health.IsAvailable;
                result.Message = $"ComfyUI is {(health.IsAvailable ? "available" : "unavailable")}: {health.Status}";
                break;
            case ComfyUiOperationType.ListWorkflows:
                var workflows = await comfyUiService.GetWorkflowsAsync(cancellationToken);
                result.ResultJson = JsonSerializer.Serialize(workflows, JsonOptions);
                result.Message = $"Found {workflows.Count} ComfyUI workflow(s).";
                break;
            case ComfyUiOperationType.ReadWorkflow:
                result.WorkflowJson = await comfyUiService.GetWorkflowAsync(result.WorkflowFileName, cancellationToken);
                result.Message = $"Read workflow '{result.WorkflowFileName}'.";
                break;
            case ComfyUiOperationType.SaveWorkflow:
                await comfyUiService.SaveWorkflowAsync(result.WorkflowFileName, request.WorkflowJson, cancellationToken);
                result.WorkflowJson = request.WorkflowJson;
                result.Message = $"Saved workflow '{result.WorkflowFileName}'.";
                break;
            case ComfyUiOperationType.BackupWorkflows:
                var backupPath = await comfyUiService.BackupWorkflowsAsync(cancellationToken);
                result.ResultJson = JsonSerializer.Serialize(new { backupPath }, JsonOptions);
                result.Message = $"Backed up ComfyUI workflows to '{backupPath}'.";
                break;
            case ComfyUiOperationType.ListModels:
                var models = await comfyUiService.GetModelsAsync(cancellationToken);
                result.ResultJson = JsonSerializer.Serialize(models, JsonOptions);
                result.Message = "Loaded ComfyUI model inventory.";
                break;
            case ComfyUiOperationType.QueueWorkflow:
                var queueResult = await comfyUiService.QueuePromptAsync(request.WorkflowJson, cancellationToken);
                result.PromptId = queueResult.PromptId;
                result.ResultJson = queueResult.Message;
                result.Message = string.IsNullOrWhiteSpace(queueResult.PromptId)
                    ? "Queued workflow, but ComfyUI did not return a prompt ID."
                    : $"Queued workflow with prompt ID '{queueResult.PromptId}'.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.OperationType), request.OperationType, "Unsupported ComfyUI operation.");
        }
    }

    private async Task<ComfyUiOperationContext> ResolveContextAsync(ComfyUiOperationResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.RunId) || string.IsNullOrWhiteSpace(result.StepId))
        {
            return ComfyUiOperationContext.Invalid("RunId and StepId are required.");
        }

        var run = await workflowRunHistoryService.GetByIdAsync(result.RunId, cancellationToken);
        if (run is null)
        {
            return ComfyUiOperationContext.Invalid($"Workflow run '{result.RunId}' was not found.");
        }

        var step = run.Steps.FirstOrDefault(item => item.StepId.Equals(result.StepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            return ComfyUiOperationContext.Invalid($"Workflow step '{result.StepId}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(run.ProjectId))
        {
            return ComfyUiOperationContext.Invalid("Workflow run does not have a project assigned.");
        }

        if (!string.IsNullOrWhiteSpace(result.ProjectId) && !result.ProjectId.Equals(run.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return ComfyUiOperationContext.Invalid("Requested project does not match the workflow run project.");
        }

        var project = run.ProjectSnapshot is not null
            ? ToProjectDefinition(run.ProjectSnapshot)
            : await projectRegistryService.GetByIdAsync(run.ProjectId, cancellationToken);
        if (project is null)
        {
            return ComfyUiOperationContext.Invalid($"Project '{run.ProjectId}' was not found.");
        }

        var agent = await agentRegistryService.GetByIdAsync(step.AgentId, cancellationToken);
        if (agent is null)
        {
            return ComfyUiOperationContext.Invalid($"Agent '{step.AgentId}' was not found.");
        }

        var profile = !string.IsNullOrWhiteSpace(agent.ExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(agent.ExecutionPermissionProfileId, cancellationToken)
            : null;
        profile ??= !string.IsNullOrWhiteSpace(project.DefaultExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(project.DefaultExecutionPermissionProfileId, cancellationToken)
            : null;

        return profile is null || !profile.Enabled
            ? ComfyUiOperationContext.Invalid("An enabled project permission profile is required for ComfyUI operations.")
            : ComfyUiOperationContext.Valid(project, profile);
    }

    private static bool ValidateRequest(ComfyUiOperationRequest request, ExecutionPermissionProfile profile, out string message)
    {
        message = string.Empty;

        if (request.OperationType is ComfyUiOperationType.ReadWorkflow or ComfyUiOperationType.SaveWorkflow
            && !IsSafeWorkflowFileName(request.WorkflowFileName))
        {
            message = "WorkflowFileName must be a safe JSON file name without a path.";
            return false;
        }

        if (request.OperationType == ComfyUiOperationType.SaveWorkflow && !profile.AllowWriteFiles)
        {
            message = $"Saving workflows is not permitted by profile '{profile.Name}'.";
            return false;
        }

        if (request.OperationType is ComfyUiOperationType.SaveWorkflow or ComfyUiOperationType.QueueWorkflow
            && !IsValidWorkflowJson(request.WorkflowJson, out message))
        {
            return false;
        }

        if (request.OperationType is ComfyUiOperationType.QueueWorkflow or ComfyUiOperationType.BackupWorkflows
            && !request.Confirmed)
        {
            message = "This ComfyUI operation requires confirmation.";
            return false;
        }

        return true;
    }

    private static bool IsSafeWorkflowFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal)
            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !fileName.Contains(Path.DirectorySeparatorChar)
            && !fileName.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool IsValidWorkflowJson(string json, out string message)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject)
            {
                message = "Workflow JSON must contain an object at the root.";
                return false;
            }

            message = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            message = $"Workflow JSON is invalid: {exception.Message}";
            return false;
        }
    }

    private async Task<ComfyUiOperationResult> PersistAsync(ComfyUiOperationResult result, CancellationToken cancellationToken)
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

    private static async Task<List<ComfyUiOperationResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ResultsPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(ResultsPath);
        return await JsonSerializer.DeserializeAsync<List<ComfyUiOperationResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<ComfyUiOperationResult> results, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
        await using var stream = File.Create(ResultsPath);
        await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken);
    }

    private static ComfyUiOperationResult Clone(ComfyUiOperationResult result)
    {
        return new ComfyUiOperationResult
        {
            Id = result.Id,
            RunId = result.RunId,
            StepId = result.StepId,
            ProjectId = result.ProjectId,
            OperationType = result.OperationType,
            WorkflowFileName = result.WorkflowFileName,
            WorkflowJson = result.WorkflowJson,
            Success = result.Success,
            Message = result.Message,
            ResultJson = result.ResultJson,
            PromptId = result.PromptId,
            ImageUrl = result.ImageUrl,
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

    private sealed record ComfyUiOperationContext(bool IsValid, string ErrorMessage, ProjectDefinition? Project, ExecutionPermissionProfile? Profile)
    {
        public static ComfyUiOperationContext Invalid(string message) => new(false, message, null, null);
        public static ComfyUiOperationContext Valid(ProjectDefinition project, ExecutionPermissionProfile profile) => new(true, string.Empty, project, profile);
    }
}
