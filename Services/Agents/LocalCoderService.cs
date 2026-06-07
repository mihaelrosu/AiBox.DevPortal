using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Models.Repositories;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Repositories;

namespace AiBox.DevPortal.Services.Agents;

public sealed class LocalCoderService(
    IOllamaService ollamaService,
    IRepositoryScannerService repositoryScannerService,
    IRepositoryFileContextService repositoryFileContextService,
    ILocalCoderHistoryService historyService) : ILocalCoderService
{
    public async Task<LocalCoderResult> CreatePlanAsync(LocalCoderTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        try
        {
            var repositoryScan = await repositoryScannerService.ScanAsync(task.RepositoryPath);
            var selectedFileContext = await repositoryFileContextService.ReadAsync(task.RepositoryPath, task.SelectedFilePaths ?? []);
            var output = await ollamaService.GenerateAsync(task.Model, BuildPlanningPrompt(task, repositoryScan, selectedFileContext));

            var result = new LocalCoderResult
            {
                Success = true,
                Output = output,
                ErrorMessage = string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            task.Status = LocalCoderTaskStatus.Planned;
            await SaveResultAsync(task, result, LocalCoderHistoryOutput.Plan);
            return result;
        }
        catch (Exception exception)
        {
            var result = new LocalCoderResult
            {
                Success = false,
                Output = string.Empty,
                ErrorMessage = $"Local planning is unavailable. Check the repository path, confirm Ollama is running, and ensure model '{task.Model}' is installed. {exception.Message}",
                CreatedAt = DateTime.UtcNow
            };
            task.Status = LocalCoderTaskStatus.Failed;
            await SaveResultAsync(task, result, LocalCoderHistoryOutput.Plan);
            return result;
        }
    }

    public async Task<LocalCoderResult> GeneratePatchAsync(LocalCoderTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var result = Placeholder("Placeholder diff only. Patch generation is not connected yet. No files were changed.");
        task.Status = LocalCoderTaskStatus.PatchGenerated;
        await SaveResultAsync(task, result, LocalCoderHistoryOutput.Diff);
        return result;
    }

    public async Task<LocalCoderResult> RunBuildAsync(LocalCoderTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var result = Placeholder("Placeholder build output only. dotnet build was not executed.");
        task.Status = LocalCoderTaskStatus.BuildSucceeded;
        await SaveResultAsync(task, result, LocalCoderHistoryOutput.Build);
        return result;
    }

    public async Task<LocalCoderResult> ReviewAsync(LocalCoderTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var result = Placeholder("Placeholder review only. Reviewer model is not connected yet.");
        task.Status = LocalCoderTaskStatus.Reviewed;
        await SaveResultAsync(task, result, LocalCoderHistoryOutput.Review);
        return result;
    }

    public async Task<LocalCoderResult> ApplyPatchAsync(LocalCoderTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var result = Placeholder("Placeholder apply only. Patch application is disabled. No files were changed.");
        task.Status = LocalCoderTaskStatus.Applied;
        await SaveResultAsync(task, result, LocalCoderHistoryOutput.Review);
        return result;
    }

    private static LocalCoderResult Placeholder(string output)
    {
        return new LocalCoderResult
        {
            Success = true,
            Output = output,
            ErrorMessage = string.Empty,
            CreatedAt = DateTime.UtcNow
        };
    }

    private async Task SaveResultAsync(LocalCoderTask task, LocalCoderResult result, LocalCoderHistoryOutput outputType)
    {
        var record = string.IsNullOrWhiteSpace(task.HistoryId)
            ? null
            : await historyService.GetByIdAsync(task.HistoryId);
        record ??= new LocalCoderTaskHistoryRecord
        {
            Id = task.HistoryId,
            CreatedAt = task.CreatedAt
        };

        record.Task = task;
        var output = result.Success ? result.Output : result.ErrorMessage;

        switch (outputType)
        {
            case LocalCoderHistoryOutput.Plan:
                record.PlanText = output;
                break;
            case LocalCoderHistoryOutput.Diff:
                record.DiffText = output;
                break;
            case LocalCoderHistoryOutput.Build:
                record.BuildOutput = output;
                break;
            case LocalCoderHistoryOutput.Review:
                record.ReviewText = output;
                break;
        }

        var saved = await historyService.SaveAsync(record);
        task.HistoryId = saved.Id;
    }

    private static string BuildPlanningPrompt(
        LocalCoderTask task,
        RepositoryScanResult repositoryScan,
        IReadOnlyList<RepositoryFileContent> selectedFileContext)
    {
        return $"""
            You are a local coding planner for AiBox.DevPortal.

            You must only create a plan.
            You must not claim that files were changed.
            You must not write full code unless needed.
            You must not produce destructive commands or shell commands that modify the system.
            You must respect allowed and forbidden paths.

            Task title:
            {task.Title}

            Repository path:
            {task.RepositoryPath}

            Model:
            {task.Model}

            Instructions:
            {task.Instructions}

            Allowed paths:
            {task.AllowedPathsText}

            Forbidden paths:
            {task.ForbiddenPathsText}

            Build command:
            {task.BuildCommand}

            Repository context:
            This context contains file and directory names only. No file contents were read.
            Scan truncated: {repositoryScan.Truncated}
            Skipped directories: {repositoryScan.SkippedDirectoryCount}
            Skipped secret-looking files: {repositoryScan.SkippedFileCount}

            Directories:
            {FormatDirectories(repositoryScan.Directories)}

            Files:
            {FormatFiles(repositoryScan.Files)}

            Selected read-only file context:
            The following selected files were read only for planning. Do not claim they were changed.
            {FormatSelectedFileContext(selectedFileContext)}

            Return:
            1. Goal summary
            2. Files likely to inspect
            3. Files likely to change
            4. Step-by-step plan
            5. Risks
            6. Acceptance checklist
            """;
    }

    private static string FormatDirectories(IReadOnlyList<string> directories)
    {
        return directories.Count == 0
            ? "(none found)"
            : string.Join(Environment.NewLine, directories.Select(path => $"- {path}"));
    }

    private static string FormatFiles(IReadOnlyList<RepositoryFileSummary> files)
    {
        return files.Count == 0
            ? "(none found)"
            : string.Join(Environment.NewLine, files.Select(file => $"- {file.RelativePath}"));
    }

    private static string FormatSelectedFileContext(IReadOnlyList<RepositoryFileContent> files)
    {
        return files.Count == 0
            ? "(none selected)"
            : string.Join(
                Environment.NewLine,
                files.Select(file => $"""
                    --- {file.RelativePath} ({file.CharacterCount} characters) ---
                    {file.Content}
                    --- end {file.RelativePath} ---
                    """));
    }

    private enum LocalCoderHistoryOutput
    {
        Plan,
        Diff,
        Build,
        Review
    }
}
