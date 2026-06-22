using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;


/// <summary>
/// Manages agent mode profiles.
/// </summary>
public sealed class AgentModeProfileService(IWebHostEnvironment environment) : IAgentModeProfileService
{
    
    /// <summary>
    /// JSON serialization options.
    /// </summary>
private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    
    /// <summary>
    /// Semaphore to lock file operations.
    /// </summary>
private static readonly SemaphoreSlim FileLock = new(1, 1);

    
    /// <summary>
    /// Gets all agent mode profiles.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation and returns a list of agent mode profiles.</returns>
public async Task<IReadOnlyList<AgentModeProfile>> GetAllAsync(CancellationToken cancellationToken = default)
{
    await FileLock.WaitAsync(cancellationToken);

    try
    {
            return (await LoadAsync(cancellationToken))
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    
    /// <summary>
    /// Gets an agent mode profile by ID.
    /// </summary>
    /// <param name="id">The ID of the agent mode profile.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation and returns an agent mode profile or null if not found.</returns>
public async Task<AgentModeProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profile = (await LoadAsync(cancellationToken))
                .FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return profile is null ? null : Clone(profile);
        }
        finally
        {
            FileLock.Release();
        }
    }

    
    /// <summary>
    /// Gets an agent mode profile by mode.
    /// </summary>
    /// <param name="mode">The mode of the agent mode profile.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation and returns an agent mode profile or null if not found.</returns>
public async Task<AgentModeProfile?> GetByModeAsync(AgentMode mode, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profile = (await LoadAsync(cancellationToken))
                .FirstOrDefault(profile => profile.Mode == mode);
            return profile is null ? null : Clone(profile);
        }
        finally
        {
            FileLock.Release();
        }
    }

    
    /// <summary>
    /// Loads agent mode profiles from storage.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation and returns a list of agent mode profiles.</returns>
private async Task<List<AgentModeProfile>> LoadAsync(CancellationToken cancellationToken)
{
    var path = GetRegistryPath();

    if (!File.Exists(path))
    {
        var profiles = CreateDefaultProfiles();
        await SaveAsync(profiles, cancellationToken);
        return profiles;
    }

    await using var stream = File.OpenRead(path);
        var loadedProfiles = await JsonSerializer.DeserializeAsync<List<AgentModeProfile>>(stream, JsonOptions, cancellationToken) ?? [];
            var normalizedProfiles = NormalizeProfiles(loadedProfiles);
        if (normalizedProfiles.Count == 0)
        {
            normalizedProfiles = CreateDefaultProfiles();
            await SaveAsync(normalizedProfiles, cancellationToken);
        }
        else if (!loadedProfiles.SequenceEqual(normalizedProfiles, AgentModeProfileComparer.Instance))
        {
            await SaveAsync(normalizedProfiles, cancellationToken);
        }

        return normalizedProfiles;
}

    
    /// <summary>
    /// Saves agent mode profiles to storage.
    /// </summary>
    /// <param name="profiles">The list of agent mode profiles.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
private async Task SaveAsync(List<AgentModeProfile> profiles, CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
    }

    
    /// <summary>
    /// Gets the registry path for agent mode profiles.
    /// </summary>
    /// <returns>The registry path.</returns>
private string GetRegistryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "agent-mode-profiles.json");
    }

    
    /// <summary>
    /// Creates default agent mode profiles.
    /// </summary>
    /// <returns>The list of default agent mode profiles.</returns>
private static List<AgentModeProfile> CreateDefaultProfiles()
    {
        return
        [
            new AgentModeProfile
            {
                Id = "planner",
                Mode = AgentMode.Planner,
                Name = "Planner",
                Model = "qwen2.5-coder:7b",
                ModelRouteId = "llamacpp-local-coder",
                PolicyId = "Planner",
                PreferredModel = "qwen2.5-coder:7b",
                FallbackModel = string.Empty,
                AllowFallback = false,
                RulesSummary = "No tools. Produce code-change tasks only."
            },
            new AgentModeProfile
            {
                Id = "patch-builder",
                Mode = AgentMode.PatchBuilder,
                Name = "Patch Builder",
                Model = "qwen2.5-coder:7b",
                ModelRouteId = "llamacpp-local-coder",
                PolicyId = "PatchBuilder",
                PreferredModel = "qwen2.5-coder:7b",
                FallbackModel = string.Empty,
                AllowFallback = false,
                RulesSummary = "Patch preview only. Return JSON edit operations."
            },
            new AgentModeProfile
            {
                Id = "verifier",
                Mode = AgentMode.Verifier,
                Name = "Verifier",
                Model = "qwen2.5-coder:7b",
                ModelRouteId = string.Empty,
                PolicyId = "Verifier",
                PreferredModel = "qwen2.5-coder:7b",
                FallbackModel = string.Empty,
                AllowFallback = false,
                RulesSummary = "Verification commands only. No file edits."
            },
            new AgentModeProfile
            {
                Id = "reviewer",
                Mode = AgentMode.Reviewer,
                Name = "Reviewer",
                Model = "qwen2.5-coder:7b",
                ModelRouteId = "llamacpp-local-coder",
                PolicyId = "Reviewer",
                PreferredModel = "qwen2.5-coder:7b",
                FallbackModel = string.Empty,
                AllowFallback = false,
                RulesSummary = "Diff review only. No commands or edits."
            },
            new AgentModeProfile
            {
                Id = "tool-runner",
                Mode = AgentMode.ToolRunner,
                Name = "Tool Runner",
                Model = "qwen2.5-coder:7b",
                ModelRouteId = string.Empty,
                PolicyId = "ToolRunner",
                PreferredModel = "qwen2.5-coder:7b",
                FallbackModel = string.Empty,
                AllowFallback = false,
                RulesSummary = "Approved tools only. No arbitrary shell usage."
            }
        ];
    }

    
    /// <summary>
    /// Clones an agent mode profile.
    /// </summary>
    /// <param name="profile">The agent mode profile to clone.</param>
    /// <returns>A new instance of the cloned agent mode profile.</returns>
private static AgentModeProfile Clone(AgentModeProfile profile)
    {
        return new AgentModeProfile
        {
            Id = profile.Id,
            Mode = profile.Mode,
            Name = profile.Name,
            Model = profile.Model,
            ModelRouteId = profile.ModelRouteId,
            PolicyId = profile.PolicyId,
            PreferredModel = profile.PreferredModel,
            FallbackModel = profile.FallbackModel,
            AllowFallback = profile.AllowFallback,
            RulesSummary = profile.RulesSummary
        };
    }

    private static List<AgentModeProfile> NormalizeProfiles(IEnumerable<AgentModeProfile> profiles)
    {
        return profiles
            .Select(profile =>
            {
                var clone = Clone(profile);

                if (string.IsNullOrWhiteSpace(clone.ModelRouteId))
                {
                    clone.ModelRouteId = clone.Mode switch
                    {
                        AgentMode.Planner => "llamacpp-local-coder",
                        AgentMode.PatchBuilder => "llamacpp-local-coder",
                        AgentMode.Reviewer => "llamacpp-local-coder",
                        _ => string.Empty
                    };
                }

                if (string.IsNullOrWhiteSpace(clone.PolicyId))
                {
                    clone.PolicyId = clone.Mode switch
                    {
                        AgentMode.Planner => "Planner",
                        AgentMode.PatchBuilder => "PatchBuilder",
                        AgentMode.Reviewer => "Reviewer",
                        AgentMode.Verifier => "Verifier",
                        AgentMode.ToolRunner => "ToolRunner",
                        _ => string.Empty
                    };
                }

                return clone;
            })
            .ToList();
    }

    private sealed class AgentModeProfileComparer : IEqualityComparer<AgentModeProfile>
    {
        public static readonly AgentModeProfileComparer Instance = new();

        public bool Equals(AgentModeProfile? x, AgentModeProfile? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return x.Id == y.Id
                   && x.Mode == y.Mode
                   && x.Name == y.Name
                   && x.Model == y.Model
                   && x.ModelRouteId == y.ModelRouteId
                   && x.PolicyId == y.PolicyId
                   && x.PreferredModel == y.PreferredModel
                   && x.FallbackModel == y.FallbackModel
                   && x.AllowFallback == y.AllowFallback
                   && x.RulesSummary == y.RulesSummary;
        }

        public int GetHashCode(AgentModeProfile obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Id);
            hash.Add(obj.Mode);
            hash.Add(obj.Name);
            hash.Add(obj.Model);
            hash.Add(obj.ModelRouteId);
            hash.Add(obj.PolicyId);
            hash.Add(obj.PreferredModel);
            hash.Add(obj.FallbackModel);
            hash.Add(obj.AllowFallback);
            hash.Add(obj.RulesSummary);
            return hash.ToHashCode();
        }
    }
}
