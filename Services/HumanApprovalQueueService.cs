using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class HumanApprovalQueueService(IWebHostEnvironment environment, AgentOrchestrationTimelineService timelineService)
{
    private const string QueueFileName = "human-approval-queue.json";
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<HumanApprovalRequest>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            await ExpireStaleRequestsAsync(items, cancellationToken);
            return items
                .OrderByDescending(item => item.RequestedAtUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<HumanApprovalRequest>> GetPendingAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            await ExpireStaleRequestsAsync(items, cancellationToken);
            return items
                .Where(item => item.Status == HumanApprovalRequestStatus.Pending)
                .OrderByDescending(item => item.RequestedAtUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<HumanApprovalRequest?> GetByIdAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            await ExpireStaleRequestsAsync(items, cancellationToken);
            var item = items.FirstOrDefault(entry => entry.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));
            return item is null ? null : Clone(item);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<HumanApprovalRequest?> GetByRunIdAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            await ExpireStaleRequestsAsync(items, cancellationToken);
            var item = items
                .Where(entry => entry.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.RequestedAtUtc)
                .FirstOrDefault();
            return item is null ? null : Clone(item);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<HumanApprovalRequest> CreateAsync(
        string runId,
        string taskName,
        RiskLevel riskLevel,
        string reason,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new ArgumentException("Task name is required.", nameof(taskName));
        }

        var request = new HumanApprovalRequest
        {
            RunId = runId.Trim(),
            TaskName = taskName.Trim(),
            RequestedAtUtc = DateTime.UtcNow,
            RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "System" : requestedBy.Trim(),
            RiskLevel = riskLevel,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Human approval requested." : reason.Trim(),
            Status = HumanApprovalRequestStatus.Pending
        };

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            items.Add(Clone(request));
            await SaveAsync(items, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }

        await timelineService.AddAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = request.RunId,
            TimestampUtc = request.RequestedAtUtc,
            EventType = AgentOrchestrationTimelineEventType.ApprovalRequested,
            StepName = "Apply",
            Message = request.Reason,
            Severity = AgentOrchestrationTimelineSeverity.Warning,
            RelatedEntityId = request.Id
        }, cancellationToken);

        return Clone(request);
    }

    public async Task<HumanApprovalRequest> DecideAsync(
        string requestId,
        HumanApprovalDecision decision,
        string decidedBy,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            await ExpireStaleRequestsAsync(items, cancellationToken);
            var item = items.FirstOrDefault(entry => entry.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Approval request not found: {requestId}");

            if (item.Status is HumanApprovalRequestStatus.Approved or HumanApprovalRequestStatus.Rejected)
            {
                return Clone(item);
            }

            item.Status = decision == HumanApprovalDecision.Approved
                ? HumanApprovalRequestStatus.Approved
                : HumanApprovalRequestStatus.Rejected;
            item.DecisionAtUtc = DateTime.UtcNow;
            item.DecisionBy = string.IsNullOrWhiteSpace(decidedBy) ? "System" : decidedBy.Trim();
            item.Notes = notes?.Trim() ?? string.Empty;

            await SaveAsync(items, cancellationToken);

            await timelineService.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = item.RunId,
                TimestampUtc = item.DecisionAtUtc ?? DateTime.UtcNow,
                EventType = decision == HumanApprovalDecision.Approved
                    ? AgentOrchestrationTimelineEventType.ApprovalGranted
                    : AgentOrchestrationTimelineEventType.ApprovalRejected,
                StepName = "Apply",
                Message = decision == HumanApprovalDecision.Approved
                    ? $"Human approval granted by {item.DecisionBy}."
                    : $"Human approval rejected by {item.DecisionBy}.",
                Severity = decision == HumanApprovalDecision.Approved
                    ? AgentOrchestrationTimelineSeverity.Info
                    : AgentOrchestrationTimelineSeverity.Error,
                RelatedEntityId = item.Id
            }, cancellationToken);

            return Clone(item);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task ExpireStaleRequestsAsync(List<HumanApprovalRequest> items, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - DefaultExpiration;
        var changed = false;

        foreach (var item in items.Where(entry => entry.Status == HumanApprovalRequestStatus.Pending && entry.RequestedAtUtc < cutoff))
        {
            item.Status = HumanApprovalRequestStatus.Expired;
            item.DecisionAtUtc = DateTime.UtcNow;
            item.DecisionBy = "System";
            item.Notes = "Request expired.";
            changed = true;
        }

        if (changed)
        {
            await SaveAsync(items, cancellationToken);
        }
    }

    private async Task<List<HumanApprovalRequest>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetQueuePath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<HumanApprovalRequest>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<HumanApprovalRequest> items, CancellationToken cancellationToken)
    {
        var path = GetQueuePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetQueuePath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", QueueFileName);
    }

    private static HumanApprovalRequest Clone(HumanApprovalRequest request)
    {
        return new HumanApprovalRequest
        {
            Id = request.Id,
            RunId = request.RunId,
            TaskName = request.TaskName,
            RequestedAtUtc = request.RequestedAtUtc,
            RequestedBy = request.RequestedBy,
            RiskLevel = request.RiskLevel,
            Reason = request.Reason,
            Status = request.Status,
            DecisionAtUtc = request.DecisionAtUtc,
            DecisionBy = request.DecisionBy,
            Notes = request.Notes
        };
    }
}
