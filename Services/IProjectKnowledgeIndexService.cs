using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IProjectKnowledgeIndexService
{
    Task<ProjectKnowledgeIndex> BuildAsync(string projectRoot, CancellationToken cancellationToken = default);
    Task<ProjectKnowledgeIndex?> GetLatestAsync(string projectRoot, CancellationToken cancellationToken = default);
}
