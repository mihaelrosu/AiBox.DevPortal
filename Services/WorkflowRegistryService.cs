using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class WorkflowRegistryService(IAgentRegistryService agentRegistry) : IWorkflowRegistryService
{
    private const string RegistryPath = "/data/workflows.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<WorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var workflows = await LoadAsync(cancellationToken);
            return workflows
                .OrderBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var workflows = await LoadAsync(cancellationToken);
            var workflow = workflows.FirstOrDefault(workflow => workflow.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return workflow is null ? null : Clone(workflow);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowDefinition> AddAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        var created = Normalize(workflow);
        created.Id = Guid.NewGuid().ToString("N");

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var workflows = await LoadAsync(cancellationToken);
            workflows.Add(created);
            await SaveAsync(workflows, cancellationToken);
            return Clone(created);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowDefinition?> UpdateAsync(string id, WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        var updated = Normalize(workflow);
        updated.Id = id;

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var workflows = await LoadAsync(cancellationToken);
            var index = workflows.FindIndex(workflow => workflow.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            workflows[index] = updated;
            await SaveAsync(workflows, cancellationToken);
            return Clone(updated);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowDefinition?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var workflows = await LoadAsync(cancellationToken);
            var workflow = workflows.FirstOrDefault(workflow => workflow.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (workflow is null)
            {
                return null;
            }

            workflow.Enabled = enabled;
            workflow.Status = enabled ? WorkflowStatus.Active : WorkflowStatus.Disabled;
            await SaveAsync(workflows, cancellationToken);
            return Clone(workflow);
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
            var workflows = await LoadAsync(cancellationToken);
            var removed = workflows.RemoveAll(workflow => workflow.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveAsync(workflows, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<WorkflowDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RegistryPath))
        {
            var workflows = new List<WorkflowDefinition> { await CreateDefaultWorkflowAsync(cancellationToken) };
            await SaveAsync(workflows, cancellationToken);
            return workflows;
        }

        await using var stream = File.OpenRead(RegistryPath);
        return await JsonSerializer.DeserializeAsync<List<WorkflowDefinition>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<WorkflowDefinition> workflows, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);

        await using var stream = File.Create(RegistryPath);
        await JsonSerializer.SerializeAsync(stream, workflows, JsonOptions, cancellationToken);
    }

    private async Task<WorkflowDefinition> CreateDefaultWorkflowAsync(CancellationToken cancellationToken)
    {
        var agents = await agentRegistry.GetAllAsync(cancellationToken);
        var stepSettings = new[]
        {
            ("Planner", PreviousResultMode.None),
            ("Architect", PreviousResultMode.LastCompletedStep),
            ("Coding Expert", PreviousResultMode.AllPreviousSteps),
            ("Implementator", PreviousResultMode.AllPreviousSteps),
            ("Verifier", PreviousResultMode.AllPreviousSteps)
        };

        var workflow = Normalize(new WorkflowDefinition
        {
            Name = "Create Portal Feature",
            Description = "Plans, designs, implements, and verifies a portal feature.",
            Enabled = true,
            Status = WorkflowStatus.Active,
            Steps = stepSettings.Select(setting => new WorkflowStepDefinition
            {
                Name = setting.Item1,
                AgentId = agents.FirstOrDefault(agent => agent.Name.Equals(setting.Item1, StringComparison.OrdinalIgnoreCase))?.Id ?? string.Empty,
                Instruction = $"Complete the {setting.Item1} step for the requested portal feature.",
                IncludePreviousResults = setting.Item2 != PreviousResultMode.None,
                PreviousResultMode = setting.Item2
            }).ToList()
        });

        workflow.Id = Guid.NewGuid().ToString("N");
        return workflow;
    }

    private static WorkflowDefinition Normalize(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            throw new ArgumentException("Workflow name is required.", nameof(workflow));
        }

        var steps = (workflow.Steps ?? [])
            .OrderBy(step => step.Order)
            .Select((step, index) => NormalizeStep(step, index + 1))
            .ToList();
        var previousStepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            step.DependsOnStepIds = step.DependsOnStepIds
                .Where(previousStepIds.Contains)
                .ToList();
            previousStepIds.Add(step.Id);
        }

        return new WorkflowDefinition
        {
            Name = workflow.Name.Trim(),
            Description = workflow.Description.Trim(),
            Enabled = workflow.Enabled,
            Status = workflow.Enabled
                ? workflow.Status == WorkflowStatus.Disabled ? WorkflowStatus.Active : workflow.Status
                : WorkflowStatus.Disabled,
            Steps = steps
        };
    }

    private static WorkflowStepDefinition NormalizeStep(WorkflowStepDefinition step, int order)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (string.IsNullOrWhiteSpace(step.Name))
        {
            throw new ArgumentException($"Workflow step {order} name is required.", nameof(step));
        }

        return new WorkflowStepDefinition
        {
            Id = string.IsNullOrWhiteSpace(step.Id) ? Guid.NewGuid().ToString("N") : step.Id,
            Order = order,
            Name = step.Name.Trim(),
            Type = step.Type,
            AgentId = step.AgentId.Trim(),
            Instruction = step.Instruction.Trim(),
            Enabled = step.Enabled,
            IncludePreviousResults = step.IncludePreviousResults,
            PreviousResultMode = Enum.IsDefined(step.PreviousResultMode)
                ? step.PreviousResultMode
                : PreviousResultMode.None,
            DependsOnStepIds = (step.DependsOnStepIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static WorkflowDefinition Clone(WorkflowDefinition workflow)
    {
        return new WorkflowDefinition
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            Enabled = workflow.Enabled,
            Status = workflow.Status,
            Steps = (workflow.Steps ?? [])
                .OrderBy(step => step.Order)
                .Select(step => new WorkflowStepDefinition
                {
                    Id = step.Id,
                    Order = step.Order,
                    Name = step.Name,
                    Type = step.Type,
                    AgentId = step.AgentId,
                    Instruction = step.Instruction,
                    Enabled = step.Enabled,
                    IncludePreviousResults = step.IncludePreviousResults,
                    PreviousResultMode = step.PreviousResultMode,
                    DependsOnStepIds = [.. (step.DependsOnStepIds ?? [])]
                })
                .ToList()
        };
    }
}
