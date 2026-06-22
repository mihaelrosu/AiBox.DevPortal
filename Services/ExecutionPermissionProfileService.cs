using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Services;

public sealed class ExecutionPermissionProfileService : IExecutionPermissionProfileService
{
    private const string RegistryPath = "Data/execution-permission-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private static readonly string[] GloballyBlockedCommandFragments =
    [
        "rm -rf /",
        "mkfs",
        "dd if=",
        "shutdown",
        "reboot"
    ];

    private static readonly string[] DockerMaintenanceBlockedCommands =
    [
        "docker system prune",
        "docker volume rm",
        "docker volume prune",
        "docker rm -f"
    ];

    public async Task<IReadOnlyList<ExecutionPermissionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
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

    public async Task<ExecutionPermissionProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profiles = await LoadAsync(cancellationToken);
            var profile = profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return profile is null ? null : Clone(profile);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ExecutionPermissionProfile> AddAsync(ExecutionPermissionProfile profile, CancellationToken cancellationToken = default)
    {
        var created = Normalize(profile);
        created.Id = Guid.NewGuid().ToString("N");

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profiles = await LoadAsync(cancellationToken);
            profiles.Add(created);
            await SaveAsync(profiles, cancellationToken);
            return Clone(created);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ExecutionPermissionProfile?> UpdateAsync(string id, ExecutionPermissionProfile profile, CancellationToken cancellationToken = default)
    {
        var updated = Normalize(profile);
        updated.Id = id;

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profiles = await LoadAsync(cancellationToken);
            var index = profiles.FindIndex(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            profiles[index] = updated;
            await SaveAsync(profiles, cancellationToken);
            return Clone(updated);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ExecutionPermissionProfile?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profiles = await LoadAsync(cancellationToken);
            var profile = profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (profile is null)
            {
                return null;
            }

            profile.Enabled = enabled;
            await SaveAsync(profiles, cancellationToken);
            return Clone(profile);
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
            var profiles = await LoadAsync(cancellationToken);
            var removed = profiles.RemoveAll(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveAsync(profiles, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<ExecutionPermissionProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RegistryPath))
        {
            var profiles = CreateDefaultProfiles();
            await SaveAsync(profiles, cancellationToken);
            return profiles;
        }

        return await JsonFileStore.LoadListAsync(RegistryPath, JsonOptions, cancellationToken, CreateDefaultProfiles);
    }

    private static async Task SaveAsync(List<ExecutionPermissionProfile> profiles, CancellationToken cancellationToken)
    {
        await JsonFileStore.SaveListAsync(RegistryPath, profiles, JsonOptions, cancellationToken);
    }

    private static List<ExecutionPermissionProfile> CreateDefaultProfiles()
    {
        return
        [
            new ExecutionPermissionProfile
            {
                Id = "no-execution",
                Name = "No Execution",
                Description = "Read-only task preparation with no shell or file access.",
                Level = ExecutionPermissionLevel.None,
                Enabled = true,
                AllowReadFiles = false,
                AllowWriteFiles = false,
                AllowCreateFiles = false,
                AllowDeleteFiles = false,
                AllowRunShell = false,
                AllowRunDotNet = false,
                AllowRunDocker = false,
                AllowRunGit = false,
                AllowRunPython = false,
                AllowNetworkAccess = false,
                RequiresConfirmation = false
            },
            new ExecutionPermissionProfile
            {
                Id = "read-only",
                Name = "Read Only",
                Description = "Can read project files without making changes or running commands.",
                Level = ExecutionPermissionLevel.ReadOnly,
                Enabled = true,
                AllowReadFiles = true,
                AllowWriteFiles = false,
                AllowCreateFiles = false,
                AllowDeleteFiles = false,
                AllowRunShell = false,
                AllowRunDotNet = false,
                AllowRunDocker = false,
                AllowRunGit = false,
                AllowRunPython = false,
                AllowNetworkAccess = false,
                RequiresConfirmation = false
            },
            new ExecutionPermissionProfile
            {
                Id = "project-write",
                Name = "Project Write",
                Description = "Can read, write, and create files inside the selected project path.",
                Level = ExecutionPermissionLevel.ProjectWrite,
                Enabled = true,
                AllowReadFiles = true,
                AllowWriteFiles = true,
                AllowCreateFiles = true,
                AllowDeleteFiles = false,
                AllowRunShell = false,
                AllowRunDotNet = false,
                AllowRunDocker = false,
                AllowRunGit = false,
                AllowRunPython = false,
                AllowNetworkAccess = false,
                RequiresConfirmation = false
            },
            new ExecutionPermissionProfile
            {
                Id = "build-and-test",
                Name = "Build And Test",
                Description = "Can modify project files and run build/test verification commands.",
                Level = ExecutionPermissionLevel.BuildAndTest,
                Enabled = true,
                AllowReadFiles = true,
                AllowWriteFiles = true,
                AllowCreateFiles = true,
                AllowDeleteFiles = false,
                AllowRunShell = true,
                AllowRunDotNet = true,
                AllowRunDocker = false,
                AllowRunGit = true,
                AllowRunPython = false,
                AllowNetworkAccess = false,
                AllowedCommands =
                [
                    "dotnet build",
                    "dotnet test",
                    "git status",
                    "git diff"
                ],
                RequiresConfirmation = false
            },
            new ExecutionPermissionProfile
            {
                Id = "docker-maintenance",
                Name = "Docker Maintenance",
                Description = "Restricted Docker maintenance with confirmation required for potentially impactful actions.",
                Level = ExecutionPermissionLevel.DockerMaintenance,
                Enabled = true,
                AllowReadFiles = true,
                AllowWriteFiles = true,
                AllowCreateFiles = true,
                AllowDeleteFiles = false,
                AllowRunShell = true,
                AllowRunDotNet = false,
                AllowRunDocker = true,
                AllowRunGit = false,
                AllowRunPython = false,
                AllowNetworkAccess = false,
                AllowedCommands =
                [
                    "docker ps",
                    "docker compose ps",
                    "docker compose build",
                    "docker compose up -d",
                    "docker compose down",
                    "docker compose logs",
                    "docker inspect",
                    "docker start",
                    "docker stop",
                    "docker restart"
                ],
                BlockedCommands = [.. DockerMaintenanceBlockedCommands],
                RequiresConfirmation = true
            },
            new ExecutionPermissionProfile
            {
                Id = "full-local-admin",
                Name = "Full Local Admin",
                Description = "Dangerous unrestricted local access. Disabled by default and requires confirmation.",
                Level = ExecutionPermissionLevel.FullLocalAdmin,
                Enabled = false,
                AllowReadFiles = true,
                AllowWriteFiles = true,
                AllowCreateFiles = true,
                AllowDeleteFiles = true,
                AllowRunShell = true,
                AllowRunDotNet = true,
                AllowRunDocker = true,
                AllowRunGit = true,
                AllowRunPython = true,
                AllowNetworkAccess = true,
                RequiresConfirmation = true
            }
        ];
    }

    private static ExecutionPermissionProfile Normalize(ExecutionPermissionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Profile name is required.", nameof(profile));
        }

        var normalized = new ExecutionPermissionProfile
        {
            Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim(),
            Name = profile.Name.Trim(),
            Description = profile.Description.Trim(),
            Level = profile.Level,
            Enabled = profile.Enabled,
            AllowReadFiles = profile.AllowReadFiles,
            AllowWriteFiles = profile.AllowWriteFiles,
            AllowCreateFiles = profile.AllowCreateFiles,
            AllowDeleteFiles = profile.AllowDeleteFiles,
            AllowRunShell = profile.AllowRunShell,
            AllowRunDotNet = profile.AllowRunDotNet,
            AllowRunDocker = profile.AllowRunDocker,
            AllowRunGit = profile.AllowRunGit,
            AllowRunPython = profile.AllowRunPython,
            AllowNetworkAccess = profile.AllowNetworkAccess,
            AllowedWorkingDirectories = NormalizeList(profile.AllowedWorkingDirectories),
            BlockedWorkingDirectories = NormalizeList(profile.BlockedWorkingDirectories),
            AllowedCommands = NormalizeList(profile.AllowedCommands),
            BlockedCommands = NormalizeList(profile.BlockedCommands),
            RequiresConfirmation = profile.RequiresConfirmation || profile.AllowDeleteFiles || profile.Level == ExecutionPermissionLevel.DockerMaintenance || profile.Level == ExecutionPermissionLevel.FullLocalAdmin
        };

        if (normalized.AllowDeleteFiles && !normalized.RequiresConfirmation)
        {
            throw new ArgumentException("Profiles that allow delete files must require confirmation.", nameof(profile));
        }

        if (normalized.Level == ExecutionPermissionLevel.DockerMaintenance)
        {
            normalized.BlockedCommands = [.. normalized.BlockedCommands.Union(DockerMaintenanceBlockedCommands, StringComparer.OrdinalIgnoreCase)];
        }

        EnsureBlockedCommandSafety(normalized.AllowedCommands);

        return normalized;
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void EnsureBlockedCommandSafety(IEnumerable<string> commands)
    {
        foreach (var command in commands)
        {
            foreach (var fragment in GloballyBlockedCommandFragments)
            {
                if (command.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Command '{command}' is not permitted because it contains a blocked pattern '{fragment}'.");
                }
            }
        }
    }

    private static ExecutionPermissionProfile Clone(ExecutionPermissionProfile profile)
    {
        return new ExecutionPermissionProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            Level = profile.Level,
            Enabled = profile.Enabled,
            AllowReadFiles = profile.AllowReadFiles,
            AllowWriteFiles = profile.AllowWriteFiles,
            AllowCreateFiles = profile.AllowCreateFiles,
            AllowDeleteFiles = profile.AllowDeleteFiles,
            AllowRunShell = profile.AllowRunShell,
            AllowRunDotNet = profile.AllowRunDotNet,
            AllowRunDocker = profile.AllowRunDocker,
            AllowRunGit = profile.AllowRunGit,
            AllowRunPython = profile.AllowRunPython,
            AllowNetworkAccess = profile.AllowNetworkAccess,
            AllowedWorkingDirectories = [.. profile.AllowedWorkingDirectories],
            BlockedWorkingDirectories = [.. profile.BlockedWorkingDirectories],
            AllowedCommands = [.. profile.AllowedCommands],
            BlockedCommands = [.. profile.BlockedCommands],
            RequiresConfirmation = profile.RequiresConfirmation
        };
    }
}
