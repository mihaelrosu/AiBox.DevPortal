using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface IAgentModeProfileService
{
    Task<IReadOnlyList<AgentModeProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<AgentModeProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<AgentModeProfile?> GetByModeAsync(AgentMode mode, CancellationToken cancellationToken = default);
}
