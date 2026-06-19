using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ProjectHistoryIndexService(IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<ProjectHistoryIndex> LoadAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(ProjectHistoryIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            await SaveUnlockedAsync(NormalizeIndex(index), cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ProjectHistoryIndex> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var rebuilt = await BuildIndexAsync(cancellationToken);
            await SaveUnlockedAsync(rebuilt, cancellationToken);
            return CloneIndex(rebuilt);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ProjectHistorySummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return BuildSummary(await LoadUnlockedAsync(cancellationToken));
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AddOrUpdateItemAsync(ProjectHistoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var index = await LoadUnlockedAsync(cancellationToken);
            var normalizedItem = NormalizeItem(item);
            var existingIndex = index.Items.FindIndex(existing => existing.Id.Equals(normalizedItem.Id, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                normalizedItem.CreatedAt = index.Items[existingIndex].CreatedAt == default
                    ? normalizedItem.CreatedAt
                    : index.Items[existingIndex].CreatedAt;
                normalizedItem.UpdatedAt = DateTime.UtcNow;
                index.Items[existingIndex] = normalizedItem;
            }
            else
            {
                if (normalizedItem.CreatedAt == default)
                {
                    normalizedItem.CreatedAt = DateTime.UtcNow;
                }

                if (normalizedItem.UpdatedAt == default)
                {
                    normalizedItem.UpdatedAt = normalizedItem.CreatedAt;
                }

                index.Items.Add(normalizedItem);
            }

            index.GeneratedAt = DateTime.UtcNow;
            index.Items = SortItems(index.Items);
            await SaveUnlockedAsync(index, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<ProjectHistoryIndex> BuildIndexAsync(CancellationToken cancellationToken)
    {
        var projectRoot = environment.ContentRootPath;
        var items = new List<ProjectHistoryItem>();

        items.AddRange(await ScanDataJsonFilesAsync(projectRoot, cancellationToken));
        items.AddRange(await ScanMarkdownFileAsync(projectRoot, "Docs/Roadmap.md", "Roadmap", cancellationToken));
        items.AddRange(await ScanMarkdownFileAsync(projectRoot, "Docs/CompletedWork.md", "CompletedWork", cancellationToken));
        items.AddRange(await ScanMarkdownFileAsync(projectRoot, "Docs/Architecture.md", "Architecture", cancellationToken));

        return new ProjectHistoryIndex
        {
            ProjectPath = projectRoot,
            GeneratedAt = DateTime.UtcNow,
            Items = SortItems(items)
        };
    }

    private async Task<IReadOnlyList<ProjectHistoryItem>> ScanDataJsonFilesAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var dataDirectory = Path.Combine(projectRoot, "Data");
        if (!Directory.Exists(dataDirectory))
        {
            return [];
        }

        var items = new List<ProjectHistoryItem>();
        foreach (var filePath in Directory.EnumerateFiles(dataDirectory, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = ToRelativePath(projectRoot, filePath);
            if (relativePath.Equals("Data/project-history-index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(filePath);
                using var document = await JsonDocument.ParseAsync(stream, JsonDocumentOptions, cancellationToken);
                var sourceType = GetJsonSourceType(relativePath);
                var fileStamp = File.GetLastWriteTimeUtc(filePath);

                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var ordinal = 0;
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        items.Add(CreateItemFromJsonElement(element, relativePath, sourceType, fileStamp, ordinal++));
                    }
                }
                else
                {
                    items.Add(CreateItemFromJsonElement(document.RootElement, relativePath, sourceType, fileStamp, 0));
                }
            }
            catch
            {
                continue;
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<ProjectHistoryItem>> ScanMarkdownFileAsync(
        string projectRoot,
        string relativePath,
        string sourceType,
        CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(filePath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        var items = new List<ProjectHistoryItem>();
        var currentHeading = Path.GetFileNameWithoutExtension(filePath);
        var currentSourceType = NormalizeDocumentationSourceType(sourceType);
        var fileStamp = File.GetLastWriteTimeUtc(filePath);
        var currentStatus = currentSourceType.Equals("CompletedWork", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Planned";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseMarkdownHeading(line, out var headingLevel, out var headingText))
            {
                currentHeading = NormalizeText(headingText);
                currentSourceType = ClassifyDocumentationSection(currentHeading, currentSourceType);
                currentStatus = InferMarkdownStatus(currentHeading, currentSourceType, currentStatus);

                if (ShouldIndexHeadingItem(currentHeading, currentSourceType))
                {
                    items.Add(CreateMarkdownItem(
                        relativePath,
                        currentSourceType,
                        currentHeading,
                        currentHeading,
                        currentStatus,
                        fileStamp,
                        BuildMarkdownTags(currentSourceType, currentStatus, $"heading-{headingLevel}")));
                }
                continue;
            }

            if (TryParseMarkdownBullet(line, out var bulletText))
            {
                var normalizedBulletText = NormalizeText(bulletText);
                normalizedBulletText = StripRecommendationSectionPrefix(normalizedBulletText, currentHeading, currentSourceType);
                var bulletStatus = InferMarkdownStatus(normalizedBulletText, currentSourceType, currentStatus);
                items.Add(CreateMarkdownItem(
                    relativePath,
                    currentSourceType,
                    currentHeading,
                    normalizedBulletText,
                    bulletStatus,
                    fileStamp,
                    BuildMarkdownTags(currentSourceType, bulletStatus, "bullet")));
            }
        }

        return items;
    }

    private static ProjectHistoryItem CreateItemFromJsonElement(JsonElement element, string relativePath, string sourceType, DateTime fileStamp, int ordinal)
    {
        var title = NormalizeText(GetFirstString(element, "title", "name", "workflowName", "projectName", "actionKey", "id", "message"));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"{Path.GetFileNameWithoutExtension(relativePath)} #{ordinal + 1}";
        }

        var summary = NormalizeText(GetFirstString(element, "summary", "description", "message", "statusMessage", "resultText", "errorMessage", "notes"));
        var status = GetJsonStatus(element, sourceType);
        var createdAt = GetFirstDateTime(element, fileStamp, "createdAt", "created", "timestamp", "generatedAt", "startedAt", "completedAt");
        var updatedAt = GetFirstDateTime(element, createdAt, "updatedAt", "modifiedAt", "lastModified", "finishedAt");

        return new ProjectHistoryItem
        {
            Id = BuildItemId(relativePath, sourceType, title, summary, status, createdAt, updatedAt, ordinal),
            SourceType = sourceType,
            Title = Truncate(title, 160),
            Summary = Truncate(summary, 280),
            FilePath = NormalizeStoredPath(relativePath),
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Tags = BuildTags(sourceType, status, relativePath, element)
        };
    }

    private static ProjectHistoryItem CreateMarkdownItem(
        string relativePath,
        string sourceType,
        string section,
        string text,
        string status,
        DateTime fileStamp,
        IReadOnlyList<string> tags)
    {
        var normalizedSection = NormalizeTitleForIndex(section);
        var normalizedText = NormalizeText(text);

        return new ProjectHistoryItem
        {
            Id = BuildItemId(relativePath, sourceType, normalizedSection, normalizedText, status, fileStamp, fileStamp, 0),
            SourceType = sourceType,
            Title = Truncate(normalizedSection, 160),
            Summary = Truncate(normalizedText, 280),
            FilePath = NormalizeStoredPath(relativePath),
            Status = status,
            CreatedAt = fileStamp,
            UpdatedAt = fileStamp,
            Tags = [.. tags.Select(tag => tag.Trim()).Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
    }

    private static ProjectHistorySummary BuildSummary(ProjectHistoryIndex index)
    {
        var items = index.Items ?? [];

        var completed = items
            .Where(item => item.SourceType.Equals("CompletedWork", StringComparison.OrdinalIgnoreCase) ||
                           IsAnyStatus(item.Status, "Completed", "Done"))
            .Select(DescribeItem)
            .Select(NormalizeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pending = items
            .Where(item => item.SourceType.Equals("Roadmap", StringComparison.OrdinalIgnoreCase) ||
                           IsAnyStatus(item.Status, "Planned", "In Progress"))
            .Select(DescribeItem)
            .Select(NormalizeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failed = items
            .Where(item => item.SourceType.Equals("KnownIssue", StringComparison.OrdinalIgnoreCase) ||
                           IsAnyStatus(item.Status, "Failed", "Blocked"))
            .Select(DescribeItem)
            .Select(NormalizeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var appliedPatches = items
            .Where(item => item.SourceType.Equals("PatchHistory", StringComparison.OrdinalIgnoreCase) ||
                           IsAnyStatus(item.Status, "Applied"))
            .Select(DescribeItem)
            .Select(NormalizeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recommended = items
            .Where(item => item.SourceType.Equals("Recommendation", StringComparison.OrdinalIgnoreCase) ||
                           item.Tags.Any(tag => tag.Contains("recommend", StringComparison.OrdinalIgnoreCase)))
            .Select(DescribeItem)
            .Select(CleanRecommendationTitle)
            .Select(NormalizeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        recommended = PrioritizeRecommendations(recommended);

        if (recommended.Count == 0)
        {
            recommended = pending.Take(5).ToList();
        }

        if (recommended.Count == 0 && failed.Count > 0)
        {
            recommended = failed.Select(item => $"Investigate {item}").Take(5).ToList();
        }

        var issues = items
            .Where(item => item.SourceType.Equals("KnownIssue", StringComparison.OrdinalIgnoreCase) ||
                           item.Tags.Any(tag => tag.Contains("issue", StringComparison.OrdinalIgnoreCase)))
            .Select(DescribeItem)
            .Select(NormalizeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectHistorySummary
        {
            ProjectPath = index.ProjectPath,
            GeneratedAt = index.GeneratedAt,
            CompletedFeatures = completed,
            PendingFeatures = pending,
            FailedSlices = failed,
            AppliedPatches = appliedPatches,
            RecommendedNextSlices = recommended,
            KnownIssues = issues
        };
    }

    private static List<string> PrioritizeRecommendations(IEnumerable<string> recommendations)
    {
        var normalized = recommendations
            .Select(NormalizeText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var priorityOrder = new[]
        {
            "Model Benchmark Runs",
            "Model Comparison Runs",
            "Automatic Model Recommendation",
            "Agent Orchestration",
            "Autonomous Execution Safeguards"
        };

        var prioritized = new List<string>(priorityOrder.Length + normalized.Count);
        foreach (var priority in priorityOrder)
        {
            var match = normalized.FirstOrDefault(item => item.Contains(priority, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match) &&
                !prioritized.Any(item => item.Equals(match, StringComparison.OrdinalIgnoreCase)))
            {
                prioritized.Add(match);
            }
        }

        prioritized.AddRange(normalized.Where(item => !prioritized.Any(prioritizedItem => prioritizedItem.Equals(item, StringComparison.OrdinalIgnoreCase))));
        return prioritized;
    }

    private async Task<ProjectHistoryIndex> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = GetIndexPath();
        if (!File.Exists(path))
        {
            return CreateEmptyIndex();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var index = await JsonSerializer.DeserializeAsync<ProjectHistoryIndex>(stream, JsonOptions, cancellationToken);
            return NormalizeIndex(index ?? CreateEmptyIndex());
        }
        catch
        {
            return CreateEmptyIndex();
        }
    }

    private async Task SaveUnlockedAsync(ProjectHistoryIndex index, CancellationToken cancellationToken)
    {
        var path = GetIndexPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
    }

    private string GetIndexPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "project-history-index.json");
    }

    private static ProjectHistoryIndex CreateEmptyIndex()
    {
        return new ProjectHistoryIndex
        {
            ProjectPath = string.Empty,
            GeneratedAt = DateTime.MinValue,
            Items = []
        };
    }

    private static ProjectHistoryIndex NormalizeIndex(ProjectHistoryIndex index)
    {
        return new ProjectHistoryIndex
        {
            ProjectPath = string.IsNullOrWhiteSpace(index.ProjectPath) ? string.Empty : index.ProjectPath.Trim(),
            GeneratedAt = index.GeneratedAt == default ? DateTime.MinValue : index.GeneratedAt,
            Items = SortItems(index.Items ?? [])
        };
    }

    private static ProjectHistoryItem NormalizeItem(ProjectHistoryItem item)
    {
        return new ProjectHistoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id.Trim(),
            SourceType = string.IsNullOrWhiteSpace(item.SourceType) ? "Unknown" : item.SourceType.Trim(),
            Title = NormalizeTitleForIndex(item.Title),
            Summary = NormalizeText(item.Summary),
            FilePath = NormalizeStoredPath(item.FilePath),
            Status = NormalizeStatus(item.Status),
            CreatedAt = item.CreatedAt == default ? DateTime.UtcNow : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? (item.CreatedAt == default ? DateTime.UtcNow : item.CreatedAt) : item.UpdatedAt,
            Tags = [.. (item.Tags ?? [])
                .Select(tag => NormalizeText(tag))
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)]
        };
    }

    private static List<ProjectHistoryItem> SortItems(IEnumerable<ProjectHistoryItem> items)
    {
        return items
            .Select(NormalizeItem)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetJsonSourceType(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();

        if (normalized.Contains("/patches/"))
        {
            return "PatchHistory";
        }

        if (normalized.Contains("agent-runs"))
        {
            return "AgentRuns";
        }

        if (normalized.Contains("verification"))
        {
            return "VerificationHistory";
        }

        if (normalized.Contains("workflow"))
        {
            return "WorkflowHistory";
        }

        return "JsonData";
    }

    private static string GetJsonStatus(JsonElement element, string sourceType)
    {
        var normalizedSourceType = NormalizeDocumentationSourceType(sourceType);

        if (TryGetBoolean(element, out var applied, "applied", "isApplied") && applied)
        {
            return "Applied";
        }

        if (TryGetBoolean(element, out var rolledBack, "rolledBack", "isRolledBack") && rolledBack)
        {
            return "RolledBack";
        }

        if (TryGetBoolean(element, out var blocked, "blocked", "isBlocked") && blocked)
        {
            return "Blocked";
        }

        if (TryGetBoolean(element, out var success, "success", "passed") && success)
        {
            return normalizedSourceType.Equals("PatchHistory", StringComparison.OrdinalIgnoreCase) ? "Applied" : "Completed";
        }

        if (TryGetBoolean(element, out var failed, "failed", "isFailure", "error") && failed)
        {
            return "Failed";
        }

        var status = GetFirstString(element, "status", "state");
        return NormalizeStatus(status);
    }

    private static List<string> BuildTags(string sourceType, string status, string relativePath, JsonElement element)
    {
        var tags = new List<string>
        {
            sourceType.ToLowerInvariant(),
            NormalizeStatus(status).ToLowerInvariant(),
            Path.GetFileNameWithoutExtension(relativePath).ToLowerInvariant()
        };

        if (TryGetBoolean(element, out var hasError, "error", "failed", "isFailure") && hasError)
        {
            tags.Add("error");
        }

        if (TryGetBoolean(element, out var applied, "applied", "isApplied") && applied)
        {
            tags.Add("applied");
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildMarkdownTags(string sourceType, string status, string kind)
    {
        return
        [
            sourceType.ToLowerInvariant(),
            NormalizeStatus(status).ToLowerInvariant(),
            kind.ToLowerInvariant()
        ];
    }

    private static string ClassifyDocumentationSection(string headingText, string fallbackSourceType)
    {
        var normalized = headingText.Trim();

        if (normalized.Contains("roadmap", StringComparison.OrdinalIgnoreCase))
        {
            return "Roadmap";
        }

        if (normalized.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("done", StringComparison.OrdinalIgnoreCase))
        {
            return "CompletedWork";
        }

        if (normalized.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            return "Architecture";
        }

        if (normalized.Contains("known issue", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("known issues", StringComparison.OrdinalIgnoreCase))
        {
            return "KnownIssue";
        }

        if (normalized.Contains("recommend", StringComparison.OrdinalIgnoreCase))
        {
            return "Recommendation";
        }

        if (normalized.Contains("acceptance criteria", StringComparison.OrdinalIgnoreCase))
        {
            return "AcceptanceCriteria";
        }

        if (normalized.Contains("dependency", StringComparison.OrdinalIgnoreCase))
        {
            return "Dependency";
        }

        return NormalizeDocumentationSourceType(fallbackSourceType);
    }

    private static string NormalizeDocumentationSourceType(string sourceType)
    {
        return sourceType switch
        {
            "Roadmap" or "CompletedWork" or "Architecture" or "KnownIssue" or "Recommendation" or "AcceptanceCriteria" or "Dependency" => sourceType,
            _ => sourceType
        };
    }

    private static bool IsAnyStatus(string status, params string[] values)
    {
        return values.Any(value => status.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Unknown";
        }

        var normalized = status.Trim();
        return normalized switch
        {
            var value when value.Equals("done", StringComparison.OrdinalIgnoreCase) => "Done",
            var value when value.Equals("completed", StringComparison.OrdinalIgnoreCase) => "Completed",
            var value when value.Equals("planned", StringComparison.OrdinalIgnoreCase) => "Planned",
            var value when value.Equals("in progress", StringComparison.OrdinalIgnoreCase) => "In Progress",
            var value when value.Equals("applied", StringComparison.OrdinalIgnoreCase) => "Applied",
            var value when value.Equals("rolledback", StringComparison.OrdinalIgnoreCase) => "RolledBack",
            var value when value.Equals("rolled back", StringComparison.OrdinalIgnoreCase) => "RolledBack",
            var value when value.Equals("blocked", StringComparison.OrdinalIgnoreCase) => "Blocked",
            var value when value.Equals("failed", StringComparison.OrdinalIgnoreCase) => "Failed",
            _ => normalized
        };
    }

    private static string InferMarkdownStatus(string text, string sourceType, string fallbackStatus)
    {
        var normalizedText = text.Trim();

        if (normalizedText.Contains("status:", StringComparison.OrdinalIgnoreCase))
        {
            var statusText = normalizedText[(normalizedText.IndexOf(':') + 1)..].Trim();
            var parsed = NormalizeStatus(statusText);
            if (!string.Equals(parsed, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return parsed;
            }
        }

        if (normalizedText.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("done", StringComparison.OrdinalIgnoreCase))
        {
            return "Completed";
        }

        if (normalizedText.Contains("planned", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("todo", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("next", StringComparison.OrdinalIgnoreCase))
        {
            return "Planned";
        }

        if (normalizedText.Contains("in progress", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("progress", StringComparison.OrdinalIgnoreCase))
        {
            return "In Progress";
        }

        if (normalizedText.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "Failed";
        }

        if (normalizedText.Contains("applied", StringComparison.OrdinalIgnoreCase))
        {
            return "Applied";
        }

        if (normalizedText.Contains("rolled back", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("rolledback", StringComparison.OrdinalIgnoreCase))
        {
            return "RolledBack";
        }

        if (normalizedText.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked";
        }

        if (sourceType.Equals("CompletedWork", StringComparison.OrdinalIgnoreCase))
        {
            return "Completed";
        }

        if (sourceType.Equals("Roadmap", StringComparison.OrdinalIgnoreCase))
        {
            return "Planned";
        }

        return NormalizeStatus(fallbackStatus);
    }

    private static bool TryParseMarkdownHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        if (!line.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        level = line.TakeWhile(character => character == '#').Count();
        if (level == 0 || level > 6)
        {
            return false;
        }

        text = line[level..].Trim();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string DescribeItem(ProjectHistoryItem item)
    {
        var title = NormalizeText(item.Title);
        var summary = NormalizeText(item.Summary);

        if (string.IsNullOrWhiteSpace(title))
        {
            return summary;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return title;
        }

        var collapsedSummary = CollapseDuplicateTitlePrefix(title, summary);
        if (string.IsNullOrWhiteSpace(collapsedSummary) ||
            collapsedSummary.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return $"{title} - {collapsedSummary}";
    }

    private static string BuildItemId(
        string relativePath,
        string sourceType,
        string title,
        string summary,
        string status,
        DateTime createdAt,
        DateTime updatedAt,
        int ordinal)
    {
        var payload = $"{relativePath}|{sourceType}|{title}|{summary}|{status}|{createdAt:O}|{updatedAt:O}|{ordinal}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..Math.Max(0, maxLength - 1)]}…";
    }

    private static DateTime GetFirstDateTime(JsonElement element, DateTime fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static string GetFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            else if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return property.ToString().Trim();
            }
        }

        return string.Empty;
    }

    private static bool TryGetBoolean(JsonElement element, out bool value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryParseMarkdownBullet(string line, out string text)
    {
        text = string.Empty;

        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            text = line[2..].Trim();
            return !string.IsNullOrWhiteSpace(text);
        }

        var periodIndex = line.IndexOf('.');
        if (periodIndex > 0 &&
            int.TryParse(line[..periodIndex], out _) &&
            periodIndex + 1 < line.Length &&
            char.IsWhiteSpace(line[periodIndex + 1]))
        {
            text = line[(periodIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    private static bool ShouldIndexHeadingItem(string headingText, string sourceType)
    {
        if (string.IsNullOrWhiteSpace(headingText))
        {
            return false;
        }

        return !IsGenericHeadingText(headingText) &&
               !IsRecommendationSectionHeading(headingText) &&
               !sourceType.Equals("Roadmap", StringComparison.OrdinalIgnoreCase) &&
               !sourceType.Equals("CompletedWork", StringComparison.OrdinalIgnoreCase) &&
               !sourceType.Equals("Architecture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecommendationSectionHeading(string headingText)
    {
        return headingText.Equals("Next Recommended Work", StringComparison.OrdinalIgnoreCase) ||
               headingText.Equals("Recommended Next Work", StringComparison.OrdinalIgnoreCase) ||
               headingText.Equals("Next Recommended Slices", StringComparison.OrdinalIgnoreCase) ||
               headingText.Equals("Recommendations", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericHeadingText(string headingText)
    {
        return IsAnyText(headingText, "Completed", "Planned", "Notes", "Requirements", "Features", "Goals", "Responsibilities");
    }

    private static bool IsAnyText(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeTitleForIndex(string? value)
    {
        var normalized = NormalizeText(value);
        return string.IsNullOrWhiteSpace(normalized) ? "Untitled" : normalized;
    }

    private static string CleanRecommendationTitle(string value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var prefixes = new[]
        {
            "Next Recommended Work - ",
            "Recommended Next Work - ",
            "Next Recommended Slices - ",
            "Recommendations - "
        };

        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeText(normalized[prefix.Length..]);
            }
        }

        return normalized;
    }

    private static string StripRecommendationSectionPrefix(string value, string currentHeading, string currentSourceType)
    {
        if (!currentSourceType.Equals("Recommendation", StringComparison.OrdinalIgnoreCase) &&
            !IsRecommendationSectionHeading(currentHeading))
        {
            return value;
        }

        var sectionPrefix = NormalizeText(currentHeading);
        if (string.IsNullOrWhiteSpace(sectionPrefix))
        {
            return value;
        }

        var repeatedPrefix = $"{sectionPrefix} - ";
        if (value.StartsWith(repeatedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(value[repeatedPrefix.Length..]);
        }

        return value;
    }

    private static string CollapseDuplicateTitlePrefix(string title, string summary)
    {
        var normalizedTitle = NormalizeText(title);
        var normalizedSummary = NormalizeText(summary);

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return normalizedSummary;
        }

        if (string.IsNullOrWhiteSpace(normalizedSummary))
        {
            return string.Empty;
        }

        if (normalizedSummary.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = $"{normalizedTitle} - ";
        if (normalizedSummary.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(normalizedSummary[prefix.Length..]);
        }

        prefix = $"{normalizedTitle}: ";
        if (normalizedSummary.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(normalizedSummary[prefix.Length..]);
        }

        return normalizedSummary;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeStoredPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static string ToRelativePath(string rootPath, string path)
    {
        return Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(path)).Replace('\\', '/');
    }

    private static ProjectHistoryIndex CloneIndex(ProjectHistoryIndex index)
    {
        return new ProjectHistoryIndex
        {
            ProjectPath = index.ProjectPath,
            GeneratedAt = index.GeneratedAt,
            Items = index.Items.Select(item => new ProjectHistoryItem
            {
                Id = item.Id,
                SourceType = item.SourceType,
                Title = item.Title,
                Summary = item.Summary,
                FilePath = item.FilePath,
                Status = item.Status,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                Tags = [.. item.Tags]
            }).ToList()
        };
    }
}
