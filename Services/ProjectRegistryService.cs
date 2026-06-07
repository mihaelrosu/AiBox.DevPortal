using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ProjectRegistryService : IProjectRegistryService
{
    private const string RegistryPath = "/data/projects.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<ProjectDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ProjectDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var projects = await LoadAsync(cancellationToken);
            var project = projects.FirstOrDefault(project => project.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return project is null ? null : Clone(project);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ProjectDefinition> AddAsync(ProjectDefinition project, CancellationToken cancellationToken = default)
    {
        var created = Normalize(project);
        created.Id = Guid.NewGuid().ToString("N");

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var projects = await LoadAsync(cancellationToken);
            projects.Add(created);
            await SaveAsync(projects, cancellationToken);
            return Clone(created);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ProjectDefinition?> UpdateAsync(string id, ProjectDefinition project, CancellationToken cancellationToken = default)
    {
        var updated = Normalize(project);
        updated.Id = id;

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var projects = await LoadAsync(cancellationToken);
            var index = projects.FindIndex(project => project.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            projects[index] = updated;
            await SaveAsync(projects, cancellationToken);
            return Clone(updated);
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
            var projects = await LoadAsync(cancellationToken);
            var removed = projects.RemoveAll(project => project.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveAsync(projects, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<ProjectDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RegistryPath))
        {
            var projects = CreateDefaultProjects();
            await SaveAsync(projects, cancellationToken);
            return projects;
        }

        await using var stream = File.OpenRead(RegistryPath);
        return await JsonSerializer.DeserializeAsync<List<ProjectDefinition>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<ProjectDefinition> projects, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);

        await using var stream = File.Create(RegistryPath);
        await JsonSerializer.SerializeAsync(stream, projects, JsonOptions, cancellationToken);
    }

    private static List<ProjectDefinition> CreateDefaultProjects()
    {
        return
        [
            new ProjectDefinition
            {
                Id = "aibox-devportal",
                Name = "AiBox.DevPortal",
                Type = ProjectType.BlazorPortal,
                Description = "Blazor portal for AiBox management and workflow operations.",
                LocalPath = "/opt/ai-box/dev/projects/AiBox.DevPortal",
                GitRepository = "mihaelrosu/AiBox.DevPortal",
                DefaultBranch = "main",
                BuildCommand = "dotnet build",
                RunCommand = "dotnet run",
                TestCommand = "dotnet test",
                DefaultExecutionPermissionProfileId = "build-and-test",
                Enabled = true
            },
            new ProjectDefinition
            {
                Id = "comfyui",
                Name = "ComfyUI",
                Type = ProjectType.PythonTool,
                Description = "ComfyUI installation and workflow assets used for local image generation.",
                LocalPath = "/opt/ai-box/dev/projects/ComfyUI",
                GitRepository = "comfyanonymous/ComfyUI",
                DefaultBranch = "master",
                BuildCommand = "python -m pip install -r requirements.txt",
                RunCommand = "python main.py",
                TestCommand = "python -m pytest",
                DefaultExecutionPermissionProfileId = "project-write",
                Enabled = true
            },
            new ProjectDefinition
            {
                Id = "open-webui",
                Name = "Open WebUI",
                Type = ProjectType.DockerService,
                Description = "Open WebUI deployment and configuration.",
                LocalPath = "/opt/ai-box/dev/projects/OpenWebUI",
                GitRepository = "open-webui/open-webui",
                DefaultBranch = "main",
                BuildCommand = "docker compose build",
                RunCommand = "docker compose up -d",
                TestCommand = "docker compose ps",
                DefaultExecutionPermissionProfileId = "docker-maintenance",
                Enabled = true
            },
            new ProjectDefinition
            {
                Id = "immich",
                Name = "Immich",
                Type = ProjectType.DockerService,
                Description = "Immich deployment and configuration.",
                LocalPath = "/opt/ai-box/dev/projects/Immich",
                GitRepository = "immich-app/immich",
                DefaultBranch = "main",
                BuildCommand = "docker compose build",
                RunCommand = "docker compose up -d",
                TestCommand = "docker compose ps",
                DefaultExecutionPermissionProfileId = "docker-maintenance",
                Enabled = true
            },
            new ProjectDefinition
            {
                Id = "ledgerv2",
                Name = "LedgerV2",
                Type = ProjectType.DotNetLibrary,
                Description = "LedgerV2 .NET codebase and supporting libraries.",
                LocalPath = "/opt/ai-box/dev/projects/LedgerV2",
                GitRepository = "mihaelrosu/LedgerV2",
                DefaultBranch = "main",
                BuildCommand = "dotnet build",
                RunCommand = "dotnet run",
                TestCommand = "dotnet test",
                DefaultExecutionPermissionProfileId = "build-and-test",
                Enabled = true
            }
        ];
    }

    private static ProjectDefinition Normalize(ProjectDefinition project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            throw new ArgumentException("Project name is required.", nameof(project));
        }

        return new ProjectDefinition
        {
            Id = string.IsNullOrWhiteSpace(project.Id) ? Guid.NewGuid().ToString("N") : project.Id,
            Name = project.Name.Trim(),
            Type = project.Type,
            Description = project.Description.Trim(),
            LocalPath = project.LocalPath.Trim(),
            GitRepository = project.GitRepository.Trim(),
            DefaultBranch = project.DefaultBranch.Trim(),
            BuildCommand = project.BuildCommand.Trim(),
            RunCommand = project.RunCommand.Trim(),
            TestCommand = project.TestCommand.Trim(),
            DefaultExecutionPermissionProfileId = project.DefaultExecutionPermissionProfileId.Trim(),
            Enabled = project.Enabled
        };
    }

    private static ProjectDefinition Clone(ProjectDefinition project)
    {
        return new ProjectDefinition
        {
            Id = project.Id,
            Name = project.Name,
            Type = project.Type,
            Description = project.Description,
            LocalPath = project.LocalPath,
            GitRepository = project.GitRepository,
            DefaultBranch = project.DefaultBranch,
            BuildCommand = project.BuildCommand,
            RunCommand = project.RunCommand,
            TestCommand = project.TestCommand,
            DefaultExecutionPermissionProfileId = project.DefaultExecutionPermissionProfileId,
            Enabled = project.Enabled
        };
    }
}
