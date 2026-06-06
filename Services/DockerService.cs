using AiBox.DevPortal.Models;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace AiBox.DevPortal.Services;

public sealed class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient client;

    public DockerService()
    {
        client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }

    public async Task<List<DockerContainerInfo>> GetContainersAsync()
    {
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        });

        return containers
            .OrderBy(container => GetContainerName(container))
            .Select(container => new DockerContainerInfo
            {
                Id = container.ID,
                Name = GetContainerName(container),
                Image = container.Image,
                Status = container.Status,
                State = container.State,
                Ports = FormatPorts(container.Ports),
                Created = container.Created
            })
            .ToList();
    }

    public Task StartAsync(string containerId)
    {
        return client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
    }

    public Task StopAsync(string containerId)
    {
        return client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
    }

    public Task RestartAsync(string containerId)
    {
        return client.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters());
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private static string GetContainerName(ContainerListResponse container)
    {
        return container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..Math.Min(12, container.ID.Length)];
    }

    private static string FormatPorts(IList<Port> ports)
    {
        if (ports.Count == 0)
        {
            return "-";
        }

        return string.Join(", ", ports.Select(port =>
        {
            var privatePort = $"{port.PrivatePort}/{port.Type}";
            return port.PublicPort == 0
                ? privatePort
                : $"{port.IP}:{port.PublicPort}->{privatePort}";
        }));
    }
}
