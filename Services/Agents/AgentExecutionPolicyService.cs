using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class AgentExecutionPolicyService(IWebHostEnvironment environment)
{
    private const string FileName = "agent-execution-policies.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentExecutionPolicy>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var policies = await LoadOrSeedAsync(cancellationToken);
            return policies.Select(Clone).ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentExecutionPolicy?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var policies = await LoadOrSeedAsync(cancellationToken);
            var policy = policies.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return policy is null ? null : Clone(policy);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<AgentExecutionPolicy> policies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policies);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            await SavePoliciesAsync(policies.Select(Clone).ToList(), cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<AgentExecutionPolicy>> LoadOrSeedAsync(CancellationToken cancellationToken)
    {
        var path = GetPoliciesPath();
        if (!File.Exists(path))
        {
            var seeded = CreateSeedPolicies();
            await SavePoliciesAsync(seeded, cancellationToken);
            return seeded;
        }

        await using var stream = File.OpenRead(path);
        var policies = await JsonSerializer.DeserializeAsync<List<AgentExecutionPolicy>>(stream, JsonOptions, cancellationToken) ?? [];
        if (policies.Count == 0)
        {
            policies = CreateSeedPolicies();
            await SavePoliciesAsync(policies, cancellationToken);
        }

        return policies;
    }

    private async Task SavePoliciesAsync(List<AgentExecutionPolicy> policies, CancellationToken cancellationToken)
    {
        var path = GetPoliciesPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, policies, JsonOptions, cancellationToken);
    }

    private string GetPoliciesPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", FileName);
    }

    private static List<AgentExecutionPolicy> CreateSeedPolicies()
    {
        return
        [
            new AgentExecutionPolicy
            {
                Id = "Planner",
                Name = "Planner",
                Description = "Planner policy for model-backed planning tasks.",
                AllowModelCalls = true,
                AllowToolCalls = false,
                AllowShellCommands = false,
                AllowPatchGeneration = false,
                AllowPatchApply = false,
                RequireHumanApproval = true,
                AutoRestoreOnVerificationFailure = false,
                MaxFilesChanged = 0,
                MaxPatchOperations = 0,
                MaxExecutionMinutes = 15,
                AllowedDirectories = [],
                BlockedDirectories = []
            },
            new AgentExecutionPolicy
            {
                Id = "PatchBuilder",
                Name = "Patch Builder",
                Description = "Patch builder policy for patch preview generation.",
                AllowModelCalls = true,
                AllowToolCalls = false,
                AllowShellCommands = false,
                AllowPatchGeneration = true,
                AllowPatchApply = false,
                RequireHumanApproval = true,
                AutoRestoreOnVerificationFailure = false,
                MaxFilesChanged = 25,
                MaxPatchOperations = 100,
                MaxExecutionMinutes = 30,
                AllowedDirectories = [],
                BlockedDirectories = []
            },
            new AgentExecutionPolicy
            {
                Id = "Reviewer",
                Name = "Reviewer",
                Description = "Reviewer policy for model-backed diff review.",
                AllowModelCalls = true,
                AllowToolCalls = false,
                AllowShellCommands = false,
                AllowPatchGeneration = false,
                AllowPatchApply = false,
                RequireHumanApproval = false,
                AutoRestoreOnVerificationFailure = false,
                MaxFilesChanged = 0,
                MaxPatchOperations = 0,
                MaxExecutionMinutes = 10,
                AllowedDirectories = [],
                BlockedDirectories = []
            },
            new AgentExecutionPolicy
            {
                Id = "Verifier",
                Name = "Verifier",
                Description = "Verifier policy for shell-based validation tasks.",
                AllowModelCalls = false,
                AllowToolCalls = false,
                AllowShellCommands = true,
                AllowPatchGeneration = false,
                AllowPatchApply = false,
                RequireHumanApproval = false,
                AutoRestoreOnVerificationFailure = false,
                MaxFilesChanged = 0,
                MaxPatchOperations = 0,
                MaxExecutionMinutes = 30,
                AllowedDirectories = [],
                BlockedDirectories = []
            },
            new AgentExecutionPolicy
            {
                Id = "ToolRunner",
                Name = "Tool Runner",
                Description = "Tool runner policy for shell and tool operations.",
                AllowModelCalls = false,
                AllowToolCalls = true,
                AllowShellCommands = true,
                AllowPatchGeneration = false,
                AllowPatchApply = true,
                RequireHumanApproval = false,
                AutoRestoreOnVerificationFailure = false,
                MaxFilesChanged = 0,
                MaxPatchOperations = 0,
                MaxExecutionMinutes = 30,
                AllowedDirectories = [],
                BlockedDirectories = []
            }
        ];
    }

    private static AgentExecutionPolicy Clone(AgentExecutionPolicy policy)
    {
        return new AgentExecutionPolicy
        {
            Id = policy.Id,
            Name = policy.Name,
            Description = policy.Description,
            AllowModelCalls = policy.AllowModelCalls,
            AllowToolCalls = policy.AllowToolCalls,
            AllowShellCommands = policy.AllowShellCommands,
            AllowPatchGeneration = policy.AllowPatchGeneration,
            AllowPatchApply = policy.AllowPatchApply,
            RequireHumanApproval = policy.RequireHumanApproval,
            AutoRestoreOnVerificationFailure = policy.AutoRestoreOnVerificationFailure,
            MaxFilesChanged = policy.MaxFilesChanged,
            MaxPatchOperations = policy.MaxPatchOperations,
            MaxExecutionMinutes = policy.MaxExecutionMinutes,
            AllowedDirectories = policy.AllowedDirectories?.ToList() ?? [],
            BlockedDirectories = policy.BlockedDirectories?.ToList() ?? []
        };
    }
}
