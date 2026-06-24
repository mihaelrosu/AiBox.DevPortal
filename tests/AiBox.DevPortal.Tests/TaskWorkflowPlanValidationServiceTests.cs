using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskWorkflowPlanValidationServiceTests
{
    [Fact]
    public void Validate_EmptyGoal_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(goal: string.Empty);

        var errors = service.Validate(plan);

        Assert.Contains("Workflow goal is required.", errors);
    }

    [Fact]
    public void Validate_NoSlices_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = new TaskWorkflowPlan { Goal = "Build feature", Slices = [] };

        var errors = service.Validate(plan);

        Assert.Contains("Workflow must contain at least one slice.", errors);
    }

    [Fact]
    public void Validate_MissingSliceId_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(slice: new TaskWorkflowSlice { SliceId = string.Empty });

        var errors = service.Validate(plan);

        Assert.Contains("Slice id is required.", errors);
    }

    [Fact]
    public void Validate_MissingTitle_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(slice: new TaskWorkflowSlice { Title = string.Empty });

        var errors = service.Validate(plan);

        Assert.Contains("Slice title is required.", errors);
    }

    [Fact]
    public void Validate_MissingInstruction_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(slice: new TaskWorkflowSlice { Instruction = string.Empty });

        var errors = service.Validate(plan);

        Assert.Contains("Slice instruction is required.", errors);
    }

    [Fact]
    public void Validate_MissingRiskLevel_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(slice: new TaskWorkflowSlice { RiskLevel = string.Empty });

        var errors = service.Validate(plan);

        Assert.Contains("Slice risk level is required.", errors);
    }

    [Fact]
    public void Validate_EmptyTargetFiles_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(slice: new TaskWorkflowSlice { TargetFiles = [] });

        var errors = service.Validate(plan);

        Assert.Contains("Slice target files are required.", errors);
    }

    [Fact]
    public void Validate_TooManyTargetFiles_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = CreateValidPlan(
            slice: new TaskWorkflowSlice
            {
                TargetFiles = ["A.cs", "B.cs", "C.cs", "D.cs", "E.cs", "F.cs"]
            });

        var errors = service.Validate(plan);

        Assert.Contains("Slice modifies too many files.", errors);
    }

    [Fact]
    public void Validate_UnknownDependency_ReturnsError()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = new TaskWorkflowPlan
        {
            Goal = "Build feature",
            Slices =
            [
                new TaskWorkflowSlice
                {
                    SliceId = "slice-1",
                    Title = "Slice 1",
                    Instruction = "Do the work.",
                    TargetFiles = ["Program.cs"],
                    RiskLevel = "low",
                    DependsOnSliceIds = ["slice-2"]
                }
            ]
        };

        var errors = service.Validate(plan);

        Assert.Contains("Slice depends on unknown slice id.", errors);
    }

    [Fact]
    public void Validate_ValidWorkflow_ReturnsNoErrors()
    {
        var service = new TaskWorkflowPlanValidationService();
        var plan = new TaskWorkflowPlan
        {
            Goal = "Build feature",
            Slices =
            [
                new TaskWorkflowSlice
                {
                    SliceId = "slice-1",
                    Title = "Slice 1",
                    Instruction = "Do the work.",
                    TargetFiles = ["Program.cs"],
                    RiskLevel = "low",
                    DependsOnSliceIds = []
                },
                new TaskWorkflowSlice
                {
                    SliceId = "slice-2",
                    Title = "Slice 2",
                    Instruction = "Finish the work.",
                    TargetFiles = ["Services/FeatureService.cs"],
                    RiskLevel = "medium",
                    DependsOnSliceIds = ["slice-1"]
                }
            ]
        };

        var errors = service.Validate(plan);

        Assert.Empty(errors);
    }

    private static TaskWorkflowPlan CreateValidPlan(string? goal = null, TaskWorkflowSlice? slice = null)
    {
        return new TaskWorkflowPlan
        {
            Goal = goal ?? "Build feature",
            Slices = [slice ?? CreateValidSlice()]
        };
    }

    private static TaskWorkflowSlice CreateValidSlice()
    {
        return new TaskWorkflowSlice
        {
            SliceId = "slice-1",
            Title = "Slice 1",
            Instruction = "Do the work.",
            TargetFiles = ["Program.cs"],
            RiskLevel = "low",
            DependsOnSliceIds = []
        };
    }
}
