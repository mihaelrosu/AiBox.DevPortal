using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface IAgentRunHistoryService
{
    Task<IReadOnlyList<AgentRunRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<AgentRunRecord> AddAsync(AgentRunRecord record, CancellationToken cancellationToken = default);
}
