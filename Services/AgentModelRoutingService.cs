using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelRoutingService(
    IAgentModeProfileService agentModeProfileService,
    AgentModelRecommendationService agentModelRecommendationService,
    IWebHostEnvironment environment)
{
    private const string RoutingFileName = "agent-model-routing.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentModelAssignment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var (assignments, wasCreated) = await LoadAsync(cancellationToken);
            var (completeAssignments, changed) = await EnsureCompleteAsync(assignments, cancellationToken);
            if (wasCreated || changed)
            {
                await SaveAsync(completeAssignments, cancellationToken);
            }

            return completeAssignments
                .OrderBy(item => item.Role)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentModelAssignment?> GetByRoleAsync(AgentMode role, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var (assignments, _) = await LoadAsync(cancellationToken);
            var assignment = assignments
                .FirstOrDefault(item => item.Role == role);
            return assignment is null ? null : Clone(assignment);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentModelAssignment> UpsertAsync(AgentModelAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var (assignments, _) = await LoadAsync(cancellationToken);
            var saved = NormalizeAndClone(assignment);
            var index = assignments.FindIndex(item => item.Role == saved.Role);
            if (index >= 0)
            {
                assignments[index] = saved;
            }
            else
            {
                assignments.Add(saved);
            }

            await SaveAsync(assignments, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentModelAssignment> ResolveAsync(
        AgentMode role,
        IReadOnlyCollection<string>? availableModels = null,
        CancellationToken cancellationToken = default)
    {
        var assignment = await GetOrCreateAsync(role, cancellationToken);
        var recommendation = assignment.UseRecommendedModel
            ? await agentModelRecommendationService.GetLatestByRoleAsync(role, cancellationToken)
            : null;
        var availableSet = availableModels is null || availableModels.Count == 0
            ? null
            : new HashSet<string>(availableModels.Where(model => !string.IsNullOrWhiteSpace(model)), StringComparer.OrdinalIgnoreCase);

        assignment.SelectedModel = string.Empty;
        assignment.FallbackUsed = false;

        if (IsAvailable(assignment.PreferredModel, availableSet))
        {
            assignment.SelectedModel = assignment.PreferredModel.Trim();
            assignment.RoutingReason = "Preferred model is available.";
            return assignment;
        }

        if (recommendation is not null && recommendation.HasRecommendation && IsAvailable(recommendation.RecommendedModel, availableSet))
        {
            assignment.SelectedModel = recommendation.RecommendedModel.Trim();
            assignment.RoutingReason = assignment.UseRecommendedModel
                ? "Preferred model is unavailable; latest recommendation selected."
                : "Latest recommendation selected.";
            return assignment;
        }

        if (assignment.AllowFallback && IsAvailable(assignment.FallbackModel, availableSet))
        {
            assignment.SelectedModel = assignment.FallbackModel.Trim();
            assignment.FallbackUsed = true;
            assignment.RoutingReason = recommendation is not null && recommendation.HasRecommendation
                ? "Preferred and recommended models are unavailable; fallback model selected."
                : "Preferred model is unavailable; fallback model selected.";
            return assignment;
        }

        if (string.IsNullOrWhiteSpace(assignment.PreferredModel))
        {
            if (recommendation is not null && recommendation.HasRecommendation)
            {
                assignment.RoutingReason = assignment.AllowFallback
                    ? "No preferred model is configured; latest recommendation is unavailable."
                    : "No preferred model is configured; latest recommendation is unavailable; fallback is disabled.";
            }
            else
            {
                assignment.RoutingReason = assignment.AllowFallback
                    ? (string.IsNullOrWhiteSpace(assignment.FallbackModel)
                        ? "No preferred model is configured and no fallback model is configured."
                        : "No preferred model is configured and fallback model is unavailable.")
                    : "No preferred model is configured and fallback is disabled.";
            }
        }
        else if (assignment.AllowFallback)
        {
            if (recommendation is not null && recommendation.HasRecommendation)
            {
                assignment.RoutingReason = string.IsNullOrWhiteSpace(assignment.FallbackModel)
                    ? "Preferred model and latest recommendation are unavailable; no fallback model is configured."
                    : "Preferred model and latest recommendation are unavailable; fallback model is unavailable.";
            }
            else
            {
                assignment.RoutingReason = string.IsNullOrWhiteSpace(assignment.FallbackModel)
                    ? "Preferred model is unavailable and no fallback model is configured."
                    : "Preferred and fallback models are unavailable.";
            }
        }
        else
        {
            assignment.RoutingReason = recommendation is not null && recommendation.HasRecommendation
                ? "Preferred model and latest recommendation are unavailable; fallback is disabled."
                : "Preferred model is unavailable and fallback is disabled.";
        }

        return assignment;
    }

    public async Task<AgentModelAssignment> ApplyRecommendationAsync(AgentMode role, CancellationToken cancellationToken = default)
    {
        var assignment = await GetOrCreateAsync(role, cancellationToken);
        assignment.UseRecommendedModel = true;
        return await UpsertAsync(assignment, cancellationToken);
    }

    private async Task<AgentModelAssignment> GetOrCreateAsync(AgentMode role, CancellationToken cancellationToken)
    {
        var assignment = await GetByRoleAsync(role, cancellationToken);
        if (assignment is not null)
        {
            return assignment;
        }

        var defaults = await CreateDefaultAssignmentsAsync(cancellationToken);
        var created = defaults.First(item => item.Role == role);
        await UpsertAsync(created, cancellationToken);
        return created;
    }

    private async Task<(List<AgentModelAssignment> Assignments, bool WasCreated)> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetRoutingPath();
        if (!File.Exists(path))
        {
            return (await CreateDefaultAssignmentsAsync(cancellationToken), true);
        }

        await using var stream = File.OpenRead(path);
        var assignments = await JsonSerializer.DeserializeAsync<List<AgentModelAssignment>>(stream, JsonOptions, cancellationToken) ?? [];
        return (Normalize(assignments), false);
    }

    private async Task<(List<AgentModelAssignment> Assignments, bool Changed)> EnsureCompleteAsync(
        List<AgentModelAssignment> assignments,
        CancellationToken cancellationToken)
    {
        var defaults = await CreateDefaultAssignmentsAsync(cancellationToken);
        var byRole = assignments.ToDictionary(item => item.Role, item => item);
        var changed = false;

        foreach (var defaultAssignment in defaults)
        {
            if (byRole.ContainsKey(defaultAssignment.Role))
            {
                continue;
            }

            byRole[defaultAssignment.Role] = defaultAssignment;
            changed = true;
        }

        return (Normalize(byRole.Values), changed);
    }

    private async Task<List<AgentModelAssignment>> CreateDefaultAssignmentsAsync(CancellationToken cancellationToken)
    {
        var profiles = await agentModeProfileService.GetAllAsync(cancellationToken);
        return profiles
            .Select(profile => new AgentModelAssignment
            {
                Role = profile.Mode,
                PreferredModel = string.IsNullOrWhiteSpace(profile.PreferredModel) ? profile.Model : profile.PreferredModel,
                UseRecommendedModel = false,
                FallbackModel = profile.FallbackModel,
                AllowFallback = profile.AllowFallback
            })
            .ToList();
    }

    private async Task SaveAsync(List<AgentModelAssignment> assignments, CancellationToken cancellationToken)
    {
        var path = GetRoutingPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, assignments, JsonOptions, cancellationToken);
    }

    private string GetRoutingPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", RoutingFileName);
    }

    private static bool IsAvailable(string? model, HashSet<string>? availableModels)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (availableModels is null)
        {
            return true;
        }

        return availableModels.Contains(model.Trim());
    }

    private static List<AgentModelAssignment> Normalize(IEnumerable<AgentModelAssignment> assignments)
    {
        return assignments
            .GroupBy(item => item.Role)
            .Select(group => NormalizeAndClone(group.First()))
            .OrderBy(item => item.Role)
            .ToList();
    }

    private static AgentModelAssignment NormalizeAndClone(AgentModelAssignment assignment)
    {
        return new AgentModelAssignment
        {
            Role = assignment.Role,
            PreferredModel = assignment.PreferredModel?.Trim() ?? string.Empty,
            UseRecommendedModel = assignment.UseRecommendedModel,
            FallbackModel = assignment.FallbackModel?.Trim() ?? string.Empty,
            AllowFallback = assignment.AllowFallback,
            SelectedModel = assignment.SelectedModel?.Trim() ?? string.Empty,
            RoutingReason = assignment.RoutingReason?.Trim() ?? string.Empty,
            FallbackUsed = assignment.FallbackUsed
        };
    }

    private static AgentModelAssignment Clone(AgentModelAssignment assignment)
    {
        return NormalizeAndClone(assignment);
    }
}
