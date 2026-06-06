using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IDockerService
{
    Task<List<DockerContainerInfo>> GetContainersAsync();
    Task StartAsync(string containerId);
    Task StopAsync(string containerId);
    Task RestartAsync(string containerId);
}
