using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class FileOperationService(
    IWorkflowRunHistoryService workflowRunHistoryService,
    IProjectRegistryService projectRegistryService,
    IAgentRegistryService agentRegistryService,
    IExecutionPermissionProfileService executionPermissionProfileService) : IFileOperationService
{
    private const string ResultsPath = "/data/file-operation-results.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private static readonly string[] ForbiddenRootPrefixes =
    [
        "/",
        "/etc",
        "/boot",
        "/sys",
        "/proc",
        "/dev",
        "/var/lib/docker",
        "/home"
    ];

    public async Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new FileOperationResult
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = request.RunId?.Trim() ?? string.Empty,
            StepId = request.StepId?.Trim() ?? string.Empty,
            ProjectId = request.ProjectId?.Trim() ?? string.Empty,
            OperationType = request.OperationType,
            Path = request.Path?.Trim() ?? string.Empty,
            TargetPath = request.TargetPath?.Trim() ?? string.Empty,
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

            var operationPaths = ResolveOperationPaths(context.Project!, request, result);
            if (!operationPaths.IsValid)
            {
                result.Message = operationPaths.ErrorMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!ValidatePermission(context.Profile!, request.OperationType, request.Confirmed, out var permissionMessage))
            {
                result.Message = permissionMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (!ValidatePathSafety(context.Project!, context.Profile!, operationPaths.PrimaryPath!, operationPaths.TargetPath, out var pathMessage))
            {
                result.Message = pathMessage;
                return await PersistAsync(result, cancellationToken);
            }

            if (request.OperationType == FileOperationType.Delete && !request.Confirmed)
            {
                result.Message = "Delete operations require confirmation.";
                return await PersistAsync(result, cancellationToken);
            }

            return request.OperationType switch
            {
                FileOperationType.Read => await ExecuteReadAsync(result, operationPaths.PrimaryPath!, cancellationToken),
                FileOperationType.Write => await ExecuteWriteAsync(result, context.Project!.LocalPath, operationPaths.PrimaryPath!, request.Content, cancellationToken),
                FileOperationType.Create => await ExecuteCreateAsync(result, context.Project!.LocalPath, operationPaths.PrimaryPath!, request.Content, cancellationToken),
                FileOperationType.Rename => await ExecuteRenameAsync(result, operationPaths.PrimaryPath!, operationPaths.TargetPath!, cancellationToken),
                FileOperationType.Copy => await ExecuteCopyAsync(result, operationPaths.PrimaryPath!, operationPaths.TargetPath!, cancellationToken),
                FileOperationType.Delete => await ExecuteDeleteAsync(result, context.Project!.LocalPath, operationPaths.PrimaryPath!, cancellationToken),
                FileOperationType.ListDirectory => await ExecuteListDirectoryAsync(result, operationPaths.PrimaryPath!, cancellationToken),
                _ => await PersistAsync(SetFailure(result, "Unsupported file operation."), cancellationToken)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result.Message = "Operation cancelled.";
            return await PersistAsync(result, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Message = exception.Message;
            return await PersistAsync(result, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<FileOperationResult>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<FileOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var normalizedRunId = runId?.Trim() ?? string.Empty;
            return (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(normalizedRunId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<FileOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
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

    private async Task<OperationContext> ResolveContextAsync(FileOperationResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.RunId) || string.IsNullOrWhiteSpace(result.StepId))
        {
            return OperationContext.Invalid("RunId and StepId are required.");
        }

        var run = await workflowRunHistoryService.GetByIdAsync(result.RunId, cancellationToken);
        if (run is null)
        {
            return OperationContext.Invalid($"Workflow run '{result.RunId}' was not found.");
        }

        var step = run.Steps.FirstOrDefault(item => item.StepId.Equals(result.StepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            return OperationContext.Invalid($"Workflow step '{result.StepId}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(run.ProjectId))
        {
            return OperationContext.Invalid("Workflow run does not have a project assigned.");
        }

        var projectId = string.IsNullOrWhiteSpace(result.ProjectId) ? run.ProjectId : result.ProjectId;
        if (!projectId.Equals(run.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return OperationContext.Invalid("Requested project does not match the workflow run project.");
        }

        var project = run.ProjectSnapshot is not null
            ? ToProjectDefinition(run.ProjectSnapshot)
            : await projectRegistryService.GetByIdAsync(run.ProjectId, cancellationToken);

        if (project is null)
        {
            return OperationContext.Invalid($"Project '{run.ProjectId}' was not found.");
        }

        var agent = await agentRegistryService.GetByIdAsync(step.AgentId, cancellationToken);
        if (agent is null)
        {
            return OperationContext.Invalid($"Agent '{step.AgentId}' was not found.");
        }

        var profile = !string.IsNullOrWhiteSpace(agent.ExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(agent.ExecutionPermissionProfileId, cancellationToken)
            : null;

        profile ??= !string.IsNullOrWhiteSpace(project.DefaultExecutionPermissionProfileId)
            ? await executionPermissionProfileService.GetByIdAsync(project.DefaultExecutionPermissionProfileId, cancellationToken)
            : null;

        if (profile is null)
        {
            return OperationContext.Invalid($"No execution permission profile is configured for agent '{agent.Name}'.");
        }

        return OperationContext.Valid(run, step, project, profile);
    }

    private static OperationPathResolution ResolveOperationPaths(ProjectDefinition project, FileOperationRequest request, FileOperationResult result)
    {
        var primary = ResolvePath(project.LocalPath, request.Path, allowEmptyToProjectRoot: request.OperationType == FileOperationType.ListDirectory);
        if (!primary.IsValid)
        {
            return OperationPathResolution.Invalid(primary.ErrorMessage);
        }

        string? target = null;

        if (RequiresTargetPath(request.OperationType))
        {
            if (string.IsNullOrWhiteSpace(request.TargetPath))
            {
                return OperationPathResolution.Invalid("TargetPath is required for this operation.");
            }

            var resolvedTarget = ResolvePath(project.LocalPath, request.TargetPath, allowEmptyToProjectRoot: false);
            if (!resolvedTarget.IsValid)
            {
                return OperationPathResolution.Invalid(resolvedTarget.ErrorMessage);
            }

            target = resolvedTarget.Path;
        }

        result.Path = primary.Path ?? result.Path;
        result.TargetPath = target ?? string.Empty;
        return OperationPathResolution.Valid(primary.Path!, target);
    }

    private static bool RequiresTargetPath(FileOperationType operationType)
    {
        return operationType is FileOperationType.Rename or FileOperationType.Copy;
    }

    private static bool ValidatePermission(
        ExecutionPermissionProfile profile,
        FileOperationType operationType,
        bool confirmed,
        out string message)
    {
        message = string.Empty;

        var requiresConfirmation = profile.RequiresConfirmation && operationType != FileOperationType.Read && operationType != FileOperationType.ListDirectory;

        if (requiresConfirmation && !confirmed)
        {
            message = "This file operation requires confirmation.";
            return false;
        }

        if (operationType == FileOperationType.Delete && (!profile.AllowDeleteFiles || !confirmed))
        {
            message = "Delete operations require delete permission and confirmation.";
            return false;
        }

        var allowed = operationType switch
        {
            FileOperationType.Read => profile.AllowReadFiles,
            FileOperationType.Write => profile.AllowWriteFiles,
            FileOperationType.Create => profile.AllowCreateFiles,
            FileOperationType.Rename => profile.AllowWriteFiles || profile.AllowCreateFiles,
            FileOperationType.Copy => profile.AllowWriteFiles || profile.AllowCreateFiles,
            FileOperationType.Delete => profile.AllowDeleteFiles,
            FileOperationType.ListDirectory => profile.AllowReadFiles,
            _ => false
        };

        if (!allowed)
        {
            message = $"Operation '{operationType}' is not permitted by profile '{profile.Name}'.";
            return false;
        }

        if ((operationType is FileOperationType.Rename or FileOperationType.Copy) && !(profile.AllowWriteFiles && profile.AllowCreateFiles))
        {
            message = $"Operation '{operationType}' requires both create and write permission.";
            return false;
        }

        return true;
    }

    private static bool ValidatePathSafety(
        ProjectDefinition project,
        ExecutionPermissionProfile profile,
        string primaryPath,
        string? targetPath,
        out string message)
    {
        message = string.Empty;

        if (!IsInsideProject(project.LocalPath, primaryPath))
        {
            message = "Primary path must be inside the project local path.";
            return false;
        }

        if (targetPath is not null && !IsInsideProject(project.LocalPath, targetPath))
        {
            message = "Target path must be inside the project local path.";
            return false;
        }

        if (IsForbiddenRoot(primaryPath, project.LocalPath))
        {
            message = $"Path '{primaryPath}' is not allowed.";
            return false;
        }

        if (targetPath is not null && IsForbiddenRoot(targetPath, project.LocalPath))
        {
            message = $"Target path '{targetPath}' is not allowed.";
            return false;
        }

        if (profile.AllowedWorkingDirectories.Count > 0
            && !profile.AllowedWorkingDirectories.Any(allowed => IsInsideProject(ResolvePathBase(project.LocalPath, allowed), primaryPath)))
        {
            message = "The primary path is outside the allowed working directories.";
            return false;
        }

        if (profile.AllowedWorkingDirectories.Count > 0 && targetPath is not null
            && !profile.AllowedWorkingDirectories.Any(allowed => IsInsideProject(ResolvePathBase(project.LocalPath, allowed), targetPath)))
        {
            message = "The target path is outside the allowed working directories.";
            return false;
        }

        if (profile.BlockedWorkingDirectories.Any(blocked => IsInsideProject(ResolvePathBase(project.LocalPath, blocked), primaryPath)))
        {
            message = "The primary path is inside a blocked directory.";
            return false;
        }

        if (targetPath is not null && profile.BlockedWorkingDirectories.Any(blocked => IsInsideProject(ResolvePathBase(project.LocalPath, blocked), targetPath)))
        {
            message = "The target path is inside a blocked directory.";
            return false;
        }

        return true;
    }

    private static bool IsForbiddenRoot(string candidatePath, string projectLocalPath)
    {
        return ForbiddenRootPrefixes.Any(prefix =>
            candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !IsInsideProject(projectLocalPath, candidatePath));
    }

    private static bool IsInsideProject(string projectLocalPath, string candidatePath)
    {
        var projectRoot = Path.GetFullPath(projectLocalPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidatePath);
        return candidateFull.Equals(Path.GetFullPath(projectLocalPath), StringComparison.OrdinalIgnoreCase)
            || candidateFull.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static PathResolution ResolvePath(string projectLocalPath, string candidate, bool allowEmptyToProjectRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            if (!allowEmptyToProjectRoot)
            {
                return PathResolution.Invalid("Path is required.");
            }

            return PathResolution.Valid(Path.GetFullPath(projectLocalPath));
        }

        var full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(projectLocalPath, candidate));
        return PathResolution.Valid(full);
    }

    private static string ResolvePathBase(string projectLocalPath, string directory)
    {
        return string.IsNullOrWhiteSpace(directory)
            ? Path.GetFullPath(projectLocalPath)
            : Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(projectLocalPath, directory));
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

    private async Task<FileOperationResult> ExecuteReadAsync(FileOperationResult result, string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            result.Success = false;
            result.Message = "Read operations require a file path.";
            return await PersistAsync(result, cancellationToken);
        }

        if (!File.Exists(path))
        {
            result.Success = false;
            result.Message = $"File '{path}' was not found.";
            return await PersistAsync(result, cancellationToken);
        }

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        result.Success = true;
        result.Message = "File read successfully.";
        result.ContentPreview = Truncate(content);
        return await PersistAsync(result, cancellationToken);
    }

    private async Task<FileOperationResult> ExecuteListDirectoryAsync(FileOperationResult result, string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            result.Success = false;
            result.Message = $"Directory '{path}' was not found.";
            return await PersistAsync(result, cancellationToken);
        }

        var entries = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            entries.Add($"[DIR] {Path.GetFileName(directory)}");
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            var info = new FileInfo(file);
            entries.Add($"[FILE] {info.Name} ({info.Length} bytes)");
        }

        result.Success = true;
        result.Message = "Directory listed successfully.";
        result.ContentPreview = Truncate(string.Join(Environment.NewLine, entries));
        return await PersistAsync(result, cancellationToken);
    }

    private async Task<FileOperationResult> ExecuteCreateAsync(FileOperationResult result, string projectLocalPath, string path, string content, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            result.Success = false;
            result.Message = $"A directory already exists at '{path}'.";
            return await PersistAsync(result, cancellationToken);
        }

        if (File.Exists(path))
        {
            result.Success = false;
            result.Message = $"File '{path}' already exists.";
            return await PersistAsync(result, cancellationToken);
        }

        EnsureParentDirectory(path);
        await File.WriteAllTextAsync(path, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        result.Success = true;
        result.Message = "File created successfully.";
        result.ContentPreview = Truncate(content);
        return await PersistAsync(result, cancellationToken);
    }

    private async Task<FileOperationResult> ExecuteWriteAsync(FileOperationResult result, string projectLocalPath, string path, string content, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            result.Success = false;
            result.Message = $"Cannot overwrite directory '{path}'.";
            return await PersistAsync(result, cancellationToken);
        }

        if (File.Exists(path))
        {
            await CreateBackupAsync(projectLocalPath, path, cancellationToken);
        }

        EnsureParentDirectory(path);
        await File.WriteAllTextAsync(path, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        result.Success = true;
        result.Message = File.Exists(path) ? "File written successfully." : "File created successfully.";
        result.ContentPreview = Truncate(content);
        return await PersistAsync(result, cancellationToken);
    }

    private async Task<FileOperationResult> ExecuteRenameAsync(FileOperationResult result, string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            result.Success = false;
            result.Message = $"Target '{targetPath}' already exists.";
            return await PersistAsync(result, cancellationToken);
        }

        EnsureParentDirectory(targetPath);

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, targetPath);
        }
        else if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, targetPath);
        }
        else
        {
            result.Success = false;
            result.Message = $"Source '{sourcePath}' was not found.";
            return await PersistAsync(result, cancellationToken);
        }

        result.Success = true;
        result.Message = "Path renamed successfully.";
        result.ContentPreview = string.Empty;
        return await PersistAsync(result, cancellationToken);
    }

    private async Task<FileOperationResult> ExecuteCopyAsync(FileOperationResult result, string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            result.Success = false;
            result.Message = $"Target '{targetPath}' already exists.";
            return await PersistAsync(result, cancellationToken);
        }

        if (File.Exists(sourcePath))
        {
            EnsureParentDirectory(targetPath);
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        else if (Directory.Exists(sourcePath))
        {
            CopyDirectoryRecursive(sourcePath, targetPath);
        }
        else
        {
            result.Success = false;
            result.Message = $"Source '{sourcePath}' was not found.";
            return await PersistAsync(result, cancellationToken);
        }

        result.Success = true;
        result.Message = "Path copied successfully.";
        result.ContentPreview = string.Empty;
        return await PersistAsync(result, cancellationToken);
    }

    private async Task<FileOperationResult> ExecuteDeleteAsync(FileOperationResult result, string projectLocalPath, string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await CreateBackupAsync(projectLocalPath, path, cancellationToken);
            File.Delete(path);
            result.Success = true;
            result.Message = "File deleted successfully.";
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            result.Success = true;
            result.Message = "Directory deleted successfully.";
        }
        else
        {
            result.Success = false;
            result.Message = $"Path '{path}' was not found.";
        }

        result.ContentPreview = string.Empty;
        return await PersistAsync(result, cancellationToken);
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private async Task CreateBackupAsync(string projectLocalPath, string path, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupPath = Path.Combine(
            Path.GetFullPath(projectLocalPath),
            ".aibox",
            "backups",
            $"{timestamp}_{fileName}");

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await using var source = File.OpenRead(path);
        await using var destination = File.Create(backupPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var targetFile = Path.Combine(targetDirectory, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var targetSubdirectory = Path.Combine(targetDirectory, Path.GetFileName(directory));
            CopyDirectoryRecursive(directory, targetSubdirectory);
        }
    }

    private static string Truncate(string? content, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return content.Length <= maxLength ? content : content[..maxLength];
    }

    private async Task<FileOperationResult> PersistAsync(FileOperationResult result, CancellationToken cancellationToken)
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

    private static async Task<List<FileOperationResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ResultsPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(ResultsPath);
        return await JsonSerializer.DeserializeAsync<List<FileOperationResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<FileOperationResult> results, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);

        await using var stream = File.Create(ResultsPath);
        await JsonSerializer.SerializeAsync(stream, results, JsonOptions, cancellationToken);
    }

    private static FileOperationResult Clone(FileOperationResult result)
    {
        return new FileOperationResult
        {
            Id = result.Id,
            RunId = result.RunId,
            StepId = result.StepId,
            ProjectId = result.ProjectId,
            OperationType = result.OperationType,
            Path = result.Path,
            TargetPath = result.TargetPath,
            Success = result.Success,
            Message = result.Message,
            ContentPreview = result.ContentPreview,
            CreatedAt = result.CreatedAt
        };
    }

    private static FileOperationResult SetFailure(FileOperationResult result, string message)
    {
        result.Success = false;
        result.Message = message;
        return result;
    }

    private sealed record OperationContext(bool IsValid, string ErrorMessage, WorkflowRunRecord? Run, WorkflowRunStepRecord? Step, ProjectDefinition? Project, ExecutionPermissionProfile? Profile)
    {
        public static OperationContext Invalid(string errorMessage) => new(false, errorMessage, null, null, null, null);
        public static OperationContext Valid(WorkflowRunRecord run, WorkflowRunStepRecord step, ProjectDefinition project, ExecutionPermissionProfile profile) => new(true, string.Empty, run, step, project, profile);
    }

    private sealed record PathResolution(bool IsValid, string? Path, string ErrorMessage)
    {
        public static PathResolution Invalid(string errorMessage) => new(false, null, errorMessage);
        public static PathResolution Valid(string path) => new(true, path, string.Empty);
    }

    private sealed record OperationPathResolution(bool IsValid, string ErrorMessage, string? PrimaryPath, string? TargetPath)
    {
        public static OperationPathResolution Invalid(string errorMessage) => new(false, errorMessage, null, null);
        public static OperationPathResolution Valid(string primaryPath, string? targetPath) => new(true, string.Empty, primaryPath, targetPath);
    }
}
