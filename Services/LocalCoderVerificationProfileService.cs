using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class LocalCoderVerificationProfileService(IWebHostEnvironment environment) : ILocalCoderVerificationProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<LocalCoderVerificationProfile>> GetAllAsync(CancellationToken cancellationToken = default)
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

    public async Task<LocalCoderVerificationProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var profile = (await LoadAsync(cancellationToken))
                .FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            return profile is null ? null : Clone(profile);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<LocalCoderVerificationProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();

        if (!File.Exists(path))
        {
            var profiles = CreateDefaultProfiles();
            await SaveAsync(profiles, cancellationToken);
            return profiles;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<LocalCoderVerificationProfile>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<LocalCoderVerificationProfile> profiles, CancellationToken cancellationToken)
    {
        var path = GetRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
    }

    private string GetRegistryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "verification-profiles.json");
    }

    private static List<LocalCoderVerificationProfile> CreateDefaultProfiles()
    {
        return
        [
            new LocalCoderVerificationProfile
            {
                Id = "quick",
                Name = "Quick",
                Description = "Fast verification with a single build.",
                Commands = ["dotnet build"]
            },
            new LocalCoderVerificationProfile
            {
                Id = "tests",
                Name = "Tests",
                Description = "Build first, then run the test suite.",
                Commands = ["dotnet build", "dotnet test"]
            },
            new LocalCoderVerificationProfile
            {
                Id = "full",
                Name = "Full",
                Description = "Build, test, and check the diff for whitespace or patch issues.",
                Commands = ["dotnet build", "dotnet test", "git diff --check"]
            }
        ];
    }

    private static LocalCoderVerificationProfile Clone(LocalCoderVerificationProfile profile)
    {
        return new LocalCoderVerificationProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            Commands = [.. profile.Commands ?? []]
        };
    }
}
