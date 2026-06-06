using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class AgentRegistryService(IWebHostEnvironment environment) : IAgentRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var agent = (await LoadAsync(cancellationToken))
                .FirstOrDefault(agent => agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return agent is null ? null : Clone(agent);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentDefinition> AddAsync(AgentDefinition agent, CancellationToken cancellationToken = default)
    {
        var created = Normalize(agent);
        created.Id = Guid.NewGuid().ToString("N");

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var agents = await LoadAsync(cancellationToken);
            agents.Add(created);
            await SaveAsync(agents, cancellationToken);
            return Clone(created);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentDefinition?> UpdateAsync(string id, AgentDefinition agent, CancellationToken cancellationToken = default)
    {
        var updated = Normalize(agent);
        updated.Id = id;

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var agents = await LoadAsync(cancellationToken);
            var index = agents.FindIndex(agent => agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            agents[index] = updated;
            await SaveAsync(agents, cancellationToken);
            return Clone(updated);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentDefinition?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var agents = await LoadAsync(cancellationToken);
            var agent = agents.FirstOrDefault(agent => agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (agent is null)
            {
                return null;
            }

            agent.Enabled = enabled;
            await SaveAsync(agents, cancellationToken);
            return Clone(agent);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var agents = await LoadAsync(cancellationToken);
            var removed = agents.RemoveAll(agent => agent.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveAsync(agents, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<AgentDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();

        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentDefinition>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentDefinition> agents, CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, agents, JsonOptions, cancellationToken);
    }

    private string GetRegistryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "agents.json");
    }

    private static AgentDefinition Normalize(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            throw new ArgumentException("Agent name is required.", nameof(agent));
        }

        if (string.IsNullOrWhiteSpace(agent.Model))
        {
            throw new ArgumentException("Agent model is required.", nameof(agent));
        }

        if (agent.Temperature is < 0 or > 2)
        {
            throw new ArgumentException("Temperature must be between 0 and 2.", nameof(agent));
        }

        return new AgentDefinition
        {
            Name = agent.Name.Trim(),
            Description = agent.Description.Trim(),
            Role = agent.Role,
            Enabled = agent.Enabled,
            Model = agent.Model.Trim(),
            Temperature = agent.Temperature,
            SystemPrompt = agent.SystemPrompt.Trim(),
            Permissions = (agent.Permissions ?? []).Distinct().Order().ToList()
        };
    }

    private static AgentDefinition Clone(AgentDefinition agent)
    {
        return new AgentDefinition
        {
            Id = agent.Id,
            Name = agent.Name,
            Description = agent.Description,
            Role = agent.Role,
            Enabled = agent.Enabled,
            Model = agent.Model,
            Temperature = agent.Temperature,
            SystemPrompt = agent.SystemPrompt,
            Permissions = [.. agent.Permissions ?? []]
        };
    }
}
