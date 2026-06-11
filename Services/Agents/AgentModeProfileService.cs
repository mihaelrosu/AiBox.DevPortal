using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class AgentModeProfileService(IWebHostEnvironment environment) : IAgentModeProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

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
        return await JsonSerializer.DeserializeAsync<List<AgentModeProfile>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentModeProfile> profiles, CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
    }

    private string GetRegistryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "agent-mode-profiles.json");
    }

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
                RulesSummary = "No tools. Produce a plan only."
            },
            new AgentModeProfile
            {
                Id = "patch-builder",
                Mode = AgentMode.PatchBuilder,
                Name = "Patch Builder",
                Model = "qwen2.5-coder:7b",
                RulesSummary = "Patch preview only. Return JSON edit operations."
            },
            new AgentModeProfile
            {
                Id = "verifier",
                Mode = AgentMode.Verifier,
                Name = "Verifier",
                Model = "qwen2.5-coder:7b",
                RulesSummary = "Verification commands only. No file edits."
            },
            new AgentModeProfile
            {
                Id = "reviewer",
                Mode = AgentMode.Reviewer,
                Name = "Reviewer",
                Model = "qwen2.5-coder:7b",
                RulesSummary = "Diff review only. No commands or edits."
            },
            new AgentModeProfile
            {
                Id = "tool-runner",
                Mode = AgentMode.ToolRunner,
                Name = "Tool Runner",
                Model = "qwen2.5-coder:7b",
                RulesSummary = "Approved tools only. No arbitrary shell usage."
            }
        ];
    }

    private static AgentModeProfile Clone(AgentModeProfile profile)
    {
        return new AgentModeProfile
        {
            Id = profile.Id,
            Mode = profile.Mode,
            Name = profile.Name,
            Model = profile.Model,
            RulesSummary = profile.RulesSummary
        };
    }
}
