using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface IAgentRegistryService
{
    Task<IReadOnlyList<AgentDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<AgentDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<AgentDefinition> AddAsync(AgentDefinition agent, CancellationToken cancellationToken = default);
    Task<AgentDefinition?> UpdateAsync(string id, AgentDefinition agent, CancellationToken cancellationToken = default);
    Task<AgentDefinition?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
