using System;
using System.Collections.Generic;
domain namespace AiBox.DevPortal.Services {
    public class TaskDecompositionService {
        public TaskPlan BuildPlan(string originalRequest) {
            var plan = new TaskPlan {
                OriginalRequest = originalRequest,
                Slices = new List<TaskSlice>(),
                CreatedAtUtc = DateTime.UtcNow
            };

            // Example slice
            var taskSlice = new TaskSlice {
                Title = "Create task decomposition models and service",
                Goal = "Decompose the task into manageable pieces",
                TargetFiles = new List<string> { "Models/TaskPlan.cs", "Models/TaskSlice.cs", "Services/TaskDecompositionService.cs" },
                InstructionFiles = new List<string>(),
                AllowedChangeType = AllowedChangeType.Add,
                MustNotChange = new List<string>(),
                VerificationCommands = new List<string> { "dotnet build" }
            };

            plan.Slices.Add(taskSlice);

            return plan;
        }
    }
}