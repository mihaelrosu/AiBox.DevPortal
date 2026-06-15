using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskDecompositionService
{
    public TaskPlan BuildPlan(string originalRequest)
    {
        var request = originalRequest?.Trim() ?? string.Empty;

        return new TaskPlan
        {
            OriginalRequest = request,
            CreatedAtUtc = DateTime.UtcNow,
            Slices =
            [
                new TaskPlanSlice
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = "Implement requested task",
                    Goal = string.IsNullOrWhiteSpace(request)
                        ? "No task was provided."
                        : request,
                    Description = string.IsNullOrWhiteSpace(request)
                        ? "No task was provided."
                        : request,
                    Status = TaskSliceStatus.Pending,
                    TargetFiles = [],
                    InstructionFiles = [],
                    AllowedChangeType = AllowedChangeType.Any,
                    MustNotChange =
                    [
                        "Patch apply logic",
                        "History logic",
                        "Agent profiles"
                    ],
                    VerificationCommands =
                    [
                        "dotnet build"
                    ],
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]
        };
    }
}
