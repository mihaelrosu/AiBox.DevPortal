using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ExecutionPolicyProfileService(IWebHostEnvironment environment)
{
    private const string ProfilesFileName = "execution-policy-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<ExecutionPolicyProfile>> GetAllAsync(CancellationToken cancellationToken = default)
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

    public async Task<ExecutionPolicyProfile?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var profile = (await LoadAsync(cancellationToken))
                .FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            return profile is null ? null : Clone(profile);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ExecutionPolicyProfile> SaveAsync(ExecutionPolicyProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var saved = Normalize(profile);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadAsync(cancellationToken);
            var index = profiles.FindIndex(item => item.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                profiles[index] = saved;
            }
            else
            {
                profiles.Add(saved);
            }

            await SaveAsync(profiles, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<ExecutionPolicyProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetProfilesPath();
        if (!File.Exists(path))
        {
            var profiles = CreateDefaultProfiles();
            await SaveAsync(profiles, cancellationToken);
            return profiles;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ExecutionPolicyProfile>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<ExecutionPolicyProfile> profiles, CancellationToken cancellationToken)
    {
        var path = GetProfilesPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
    }

    private string GetProfilesPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", ProfilesFileName);
    }

    private static List<ExecutionPolicyProfile> CreateDefaultProfiles()
    {
        return
        [
            new ExecutionPolicyProfile
            {
                Name = "Safe",
                AllowAutoApply = false,
                AllowCommitAndSync = false,
                RequireHumanApprovalForHighRisk = true,
                AllowProgramCsChanges = false,
                AllowSecurityChanges = false,
                MaxChangedFiles = 3
            },
            new ExecutionPolicyProfile
            {
                Name = "Balanced",
                AllowAutoApply = false,
                AllowCommitAndSync = true,
                RequireHumanApprovalForHighRisk = true,
                AllowProgramCsChanges = true,
                AllowSecurityChanges = false,
                MaxChangedFiles = 10
            },
            new ExecutionPolicyProfile
            {
                Name = "Aggressive",
                AllowAutoApply = true,
                AllowCommitAndSync = true,
                RequireHumanApprovalForHighRisk = false,
                AllowProgramCsChanges = true,
                AllowSecurityChanges = true,
                MaxChangedFiles = int.MaxValue
            }
        ];
    }

    private static ExecutionPolicyProfile Normalize(ExecutionPolicyProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);

        return new ExecutionPolicyProfile
        {
            Name = profile.Name.Trim(),
            AllowAutoApply = profile.AllowAutoApply,
            AllowCommitAndSync = profile.AllowCommitAndSync,
            RequireHumanApprovalForHighRisk = profile.RequireHumanApprovalForHighRisk,
            AllowProgramCsChanges = profile.AllowProgramCsChanges,
            AllowSecurityChanges = profile.AllowSecurityChanges,
            MaxChangedFiles = Math.Max(0, profile.MaxChangedFiles)
        };
    }

    private static ExecutionPolicyProfile Clone(ExecutionPolicyProfile profile)
    {
        return new ExecutionPolicyProfile
        {
            Name = profile.Name,
            AllowAutoApply = profile.AllowAutoApply,
            AllowCommitAndSync = profile.AllowCommitAndSync,
            RequireHumanApprovalForHighRisk = profile.RequireHumanApprovalForHighRisk,
            AllowProgramCsChanges = profile.AllowProgramCsChanges,
            AllowSecurityChanges = profile.AllowSecurityChanges,
            MaxChangedFiles = profile.MaxChangedFiles
        };
    }
}
