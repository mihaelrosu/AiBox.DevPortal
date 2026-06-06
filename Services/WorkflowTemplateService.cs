using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class WorkflowTemplateService(
    IAgentRegistryService agentRegistry,
    IWorkflowRegistryService workflowRegistry) : IWorkflowTemplateService
{
    private const string TemplatesPath = "/data/workflow-templates.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<WorkflowTemplateDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderBy(template => template.Category)
                .ThenBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowTemplateDefinition?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var template = (await LoadAsync(cancellationToken))
                .FirstOrDefault(template => template.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return template is null ? null : Clone(template);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowDefinition?> CreateWorkflowAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await GetByIdAsync(templateId, cancellationToken);

        if (template is null)
        {
            return null;
        }

        if (!template.Enabled)
        {
            throw new ArgumentException($"Workflow template '{template.Name}' is disabled.", nameof(templateId));
        }

        var stepIds = template.Steps.ToDictionary(
            step => step.Id,
            _ => Guid.NewGuid().ToString("N"),
            StringComparer.OrdinalIgnoreCase);

        var workflow = new WorkflowDefinition
        {
            Name = template.Name,
            Description = template.Description,
            Enabled = true,
            Status = WorkflowStatus.Active,
            Steps = template.Steps
                .OrderBy(step => step.Order)
                .Select(step => new WorkflowStepDefinition
                {
                    Id = stepIds[step.Id],
                    Order = step.Order,
                    Name = step.Name,
                    Type = step.Type,
                    AgentId = step.AgentId,
                    Instruction = step.Instruction,
                    Enabled = step.Enabled,
                    IncludePreviousResults = step.IncludePreviousResults,
                    PreviousResultMode = step.PreviousResultMode,
                    DependsOnStepIds = (step.DependsOnStepIds ?? [])
                        .Where(stepIds.ContainsKey)
                        .Select(id => stepIds[id])
                        .ToList()
                })
                .ToList()
        };

        return await workflowRegistry.AddAsync(workflow, cancellationToken);
    }

    private async Task<List<WorkflowTemplateDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(TemplatesPath))
        {
            var templates = await CreateDefaultTemplatesAsync(cancellationToken);
            await SaveAsync(templates, cancellationToken);
            return templates;
        }

        await using var stream = File.OpenRead(TemplatesPath);
        return await JsonSerializer.DeserializeAsync<List<WorkflowTemplateDefinition>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(
        List<WorkflowTemplateDefinition> templates,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TemplatesPath)!);

        await using var stream = File.Create(TemplatesPath);
        await JsonSerializer.SerializeAsync(stream, templates, JsonOptions, cancellationToken);
    }

    private async Task<List<WorkflowTemplateDefinition>> CreateDefaultTemplatesAsync(CancellationToken cancellationToken)
    {
        var agents = await agentRegistry.GetAllAsync(cancellationToken);
        var agentsByName = agents.ToDictionary(agent => agent.Name, StringComparer.OrdinalIgnoreCase);

        return
        [
            CreateTemplate(
                "portal-feature",
                "Portal Feature",
                "Plans, designs, implements, and verifies an AI-Box Portal feature.",
                WorkflowTemplateCategory.PortalFeature,
                agentsByName,
                ["Planner", "Architect", "Coding Expert", "Implementator", "Verifier"]),
            CreateTemplate(
                "comfyui-workflow",
                "ComfyUI Workflow",
                "Plans, designs, implements, and verifies a ComfyUI workflow.",
                WorkflowTemplateCategory.ComfyUIWorkflow,
                agentsByName,
                ["Planner", "ComfyUI Expert", "Coding Expert", "Verifier"]),
            CreateTemplate(
                "sysadmin-task",
                "SysAdmin Task",
                "Plans, implements, and verifies a system administration task.",
                WorkflowTemplateCategory.SysAdminTask,
                agentsByName,
                ["Planner", "SysAdmin Expert", "Implementator", "Verifier"]),
            CreateTemplate(
                "coding-fix",
                "Coding Fix",
                "Implements and verifies a focused coding fix.",
                WorkflowTemplateCategory.CodingTask,
                agentsByName,
                ["Coding Expert", "Verifier"]),
            CreateTemplate(
                "image-prompt-improvement",
                "Image Prompt Improvement",
                "Improves and verifies an image generation prompt.",
                WorkflowTemplateCategory.ImagePromptTask,
                agentsByName,
                ["ComfyUI Expert", "Verifier"])
        ];
    }

    private static WorkflowTemplateDefinition CreateTemplate(
        string id,
        string name,
        string description,
        WorkflowTemplateCategory category,
        IReadOnlyDictionary<string, AgentDefinition> agentsByName,
        IReadOnlyList<string> stepNames)
    {
        return new WorkflowTemplateDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Category = category,
            Enabled = true,
            Steps = stepNames.Select((stepName, index) => new WorkflowStepDefinition
            {
                Id = $"{id}-{index + 1}",
                Order = index + 1,
                Name = stepName,
                AgentId = agentsByName.TryGetValue(stepName, out var agent) ? agent.Id : string.Empty,
                Instruction = $"Complete the {stepName} step for the {name.ToLowerInvariant()} request.",
                Enabled = true,
                IncludePreviousResults = index > 0,
                PreviousResultMode = index switch
                {
                    0 => PreviousResultMode.None,
                    1 => PreviousResultMode.LastCompletedStep,
                    _ => PreviousResultMode.AllPreviousSteps
                }
            }).ToList()
        };
    }

    private static WorkflowTemplateDefinition Clone(WorkflowTemplateDefinition template)
    {
        return new WorkflowTemplateDefinition
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description,
            Category = template.Category,
            Enabled = template.Enabled,
            Steps = (template.Steps ?? [])
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
