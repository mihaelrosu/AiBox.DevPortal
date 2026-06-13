using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class LocalCoderHistoryService(IWebHostEnvironment environment) : ILocalCoderHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<LocalCoderHistoryEntry>> GetHistoryAsync(int take = 50)
    {
        await FileLock.WaitAsync();

        try
        {
            return (await LoadAsync())
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<LocalCoderHistoryEntry?> GetEntryAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await FileLock.WaitAsync();

        try
        {
            var entry = (await LoadAsync())
                .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            return entry is null ? null : Clone(entry);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AddEntryAsync(LocalCoderHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await FileLock.WaitAsync();

        try
        {
            var entries = await LoadAsync();
            var saved = Clone(entry);

            if (string.IsNullOrWhiteSpace(saved.Id))
            {
                saved.Id = Guid.NewGuid().ToString("N");
            }

            if (saved.CreatedAt == default)
            {
                saved.CreatedAt = DateTimeOffset.UtcNow;
            }

            var index = entries.FindIndex(item => item.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                entries[index] = saved;
            }
            else
            {
                entries.Add(saved);
            }

            await SaveAllAsync(entries);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task ClearHistoryAsync()
    {
        await FileLock.WaitAsync();

        try
        {
            var path = GetHistoryPath();

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<LocalCoderHistoryEntry>> LoadAsync()
    {
        var path = GetHistoryPath();

        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<LocalCoderHistoryEntry>>(stream, JsonOptions) ?? [];
    }

    private async Task SaveAllAsync(List<LocalCoderHistoryEntry> entries)
    {
        var path = GetHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "coder-task-history.json");
    }

    private static LocalCoderHistoryEntry Clone(LocalCoderHistoryEntry entry)
    {
        return new LocalCoderHistoryEntry
        {
            Id = entry.Id,
            CreatedAt = entry.CreatedAt,
            ActionType = entry.ActionType,
            ProjectPath = entry.ProjectPath,
            Model = entry.Model,
            Task = entry.Task,
            SelectedFiles = [.. entry.SelectedFiles ?? []],
            LoadedContextFiles = [.. entry.LoadedContextFiles ?? []],
            ContextFileCount = entry.ContextFileCount,
            ContextTotalCharacters = entry.ContextTotalCharacters,
            ContextEstimatedTokens = entry.ContextEstimatedTokens,
            ContextFiles = (entry.ContextFiles ?? [])
                .Select(file => new LocalCoderHistoryContextFile
                {
                    RelativePath = file.RelativePath,
                    CharacterCount = file.CharacterCount,
                    EstimatedTokens = file.EstimatedTokens,
                    ContentHash = file.ContentHash
                })
                .ToArray(),
            PlanTasks = (entry.PlanTasks ?? [])
                .Select(task => new LocalCoderPlanTask
                {
                    Id = task.Id,
                    Order = task.Order,
                    Title = task.Title,
                    Description = task.Description,
                    Status = task.Status,
                    StatusMessage = task.StatusMessage,
                    UpdatedAt = task.UpdatedAt
                })
                .ToArray(),
            PlanText = entry.PlanText,
            PatchPreviewText = entry.PatchPreviewText,
            ScopeAnalysis = CloneScopeAnalysis(entry.ScopeAnalysis),
            Intent = CloneIntent(entry.Intent),
            IntentValidation = CloneIntentValidation(entry.IntentValidation),
            ContextCoverage = CloneContextCoverage(entry.ContextCoverage),
            ValidationErrors = [.. entry.ValidationErrors ?? []],
            OperationGrammarErrors = [.. entry.OperationGrammarErrors ?? []],
            ValidationGuidance = [.. entry.ValidationGuidance ?? []],
            ReplaceDiagnostics = [.. entry.ReplaceDiagnostics ?? []],
            SuggestedTargetDiagnostics = [.. entry.SuggestedTargetDiagnostics ?? []],
            ApplyResult = CloneApplyResult(entry.ApplyResult),
            RollbackResult = CloneRollbackResult(entry.RollbackResult),
            VerificationResults = [.. entry.VerificationResults ?? []],
            Success = entry.Success,
            Message = entry.Message
        };
    }

    private static LocalCoderPatchApplyResult? CloneApplyResult(LocalCoderPatchApplyResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return new LocalCoderPatchApplyResult
        {
            Applied = result.Applied,
            VerificationPassed = result.VerificationPassed,
            Message = result.Message,
            GitApplyResult = CloneCommand(result.GitApplyResult),
            GitDiffStatResult = CloneCommand(result.GitDiffStatResult),
            ChangedFilesDiffResult = CloneCommand(result.ChangedFilesDiffResult),
            ChangedFiles = [.. result.ChangedFiles ?? []],
            BackupFiles = [.. result.BackupFiles ?? []],
            VerificationResults = [.. result.VerificationResults ?? []]
        };
    }

    private static PatchScopeAnalysis? CloneScopeAnalysis(PatchScopeAnalysis? analysis)
    {
        if (analysis is null)
        {
            return null;
        }

        return new PatchScopeAnalysis
        {
            Mode = analysis.Mode,
            AllowedFolders = [.. analysis.AllowedFolders ?? []],
            Files = (analysis.Files ?? [])
                .Select(file => new PatchScopeFileResult
                {
                    RelativePath = file.RelativePath,
                    Status = file.Status,
                    Reason = file.Reason,
                    IsCreate = file.IsCreate,
                    ContextRepresentativePath = file.ContextRepresentativePath
                })
                .ToArray(),
            WarningMessage = analysis.WarningMessage,
            IsBlocking = analysis.IsBlocking
        };
    }

    private static PatchIntent? CloneIntent(PatchIntent? intent)
    {
        if (intent is null)
        {
            return null;
        }

        return new PatchIntent
        {
            Goal = intent.Goal,
            AllowedPaths = [.. intent.AllowedPaths ?? []],
            ProtectedPaths = [.. intent.ProtectedPaths ?? []],
            AllowedScope = intent.AllowedScope,
            ExpectedChangeType = intent.ExpectedChangeType,
            MustNotChange = [.. intent.MustNotChange ?? []],
            VerificationCommand = intent.VerificationCommand
        };
    }

    private static PatchIntentValidation? CloneIntentValidation(PatchIntentValidation? validation)
    {
        if (validation is null)
        {
            return null;
        }

        return new PatchIntentValidation
        {
            Status = validation.Status,
            DetectedChangeType = validation.DetectedChangeType,
            ScopeMode = validation.ScopeMode,
            RequestedFiles = [.. validation.RequestedFiles ?? []],
            ModifiedFiles = [.. validation.ModifiedFiles ?? []],
            ContextFiles = [.. validation.ContextFiles ?? []],
            ProtectedFiles = [.. validation.ProtectedFiles ?? []],
            FileEvaluations = (validation.FileEvaluations ?? [])
                .Select(file => new PatchIntentFileEvaluation
                {
                    RelativePath = file.RelativePath,
                    InContext = file.InContext,
                    InScope = file.InScope,
                    MatchesRequestedFile = file.MatchesRequestedFile,
                    ExplicitlyAllowed = file.ExplicitlyAllowed,
                    ExplicitlyProtected = file.ExplicitlyProtected,
                    Protected = file.Protected
                })
                .ToArray(),
            Reasons = [.. validation.Reasons ?? []]
        };
    }

    private static PatchContextCoverage? CloneContextCoverage(PatchContextCoverage? coverage)
    {
        if (coverage is null)
        {
            return null;
        }

        return new PatchContextCoverage
        {
            Files = (coverage.Files ?? [])
                .Select(file => new PatchContextCoverageFile
                {
                    RelativePath = file.RelativePath,
                    Category = file.Category,
                    RiskReasons = [.. file.RiskReasons ?? []]
                })
                .ToArray(),
            RiskScore = coverage.RiskScore
        };
    }

    private static LocalCoderPatchRollbackResult? CloneRollbackResult(LocalCoderPatchRollbackResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return new LocalCoderPatchRollbackResult
        {
            RolledBack = result.RolledBack,
            VerificationPassed = result.VerificationPassed,
            Message = result.Message,
            RestoredFiles = [.. result.RestoredFiles ?? []],
            VerificationResults = [.. result.VerificationResults ?? []]
        };
    }

    private static CommandRunResult CloneCommand(CommandRunResult result)
    {
        return new CommandRunResult
        {
            Command = result.Command,
            ExitCode = result.ExitCode,
            Output = result.Output,
            Error = result.Error,
            Started = result.Started,
            Finished = result.Finished
        };
    }
}
