using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Services.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ScheduledRunDispatcherTests
{
    [Fact]
    public async Task ProcessDueRunsAsync_CreatesAndCompletesExecutionRecord()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            InitializeGitRepository(root);
            var orchestrationService = CreateOrchestrationService(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))),
                BuildOllamaResponse("The patch looks good. Apply it."));

            using var provider = CreateServiceProvider(root, orchestrationService);

            ScheduledAgentRun createdRun;
            using (var scope = provider.CreateScope())
            {
                var runService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
                createdRun = await runService.CreateAsync(new ScheduledAgentRun
                {
                    Name = "Nightly dispatcher run",
                    Enabled = true,
                    CronExpression = "0 0 * * *",
                    TaskName = "Implement orchestration",
                    UserRequest = "Create: Models/OrchestrationExample.cs",
                    ExecutionPolicyName = "Safe",
                    CommitAndSync = true,
                    NextRunUtc = DateTime.UtcNow.AddMinutes(-5)
                });
            }

            var dispatcher = new ScheduledRunDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ScheduledRunDispatcher>.Instance);

            await InvokeProcessDueRunsAsync(dispatcher);

            using (var scope = provider.CreateScope())
            {
                var runService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
                var executionService = scope.ServiceProvider.GetRequiredService<ScheduledAgentExecutionService>();

                var updatedRun = await runService.GetByIdAsync(createdRun.Id);
                Assert.NotNull(updatedRun);
                Assert.False(updatedRun!.IsRunning);
                Assert.NotNull(updatedRun.LastStartedUtc);
                Assert.NotNull(updatedRun.LastCompletedUtc);
                Assert.Null(updatedRun.LastError);

                var executions = await executionService.GetByRunIdAsync(createdRun.Id);
                var execution = Assert.Single(executions);
                Assert.Equal(createdRun.Id, execution.ScheduledRunId);
                Assert.Equal(createdRun.Name, execution.ScheduleName);
                Assert.Equal(createdRun.TaskName, execution.TaskName);
                Assert.True(execution.Success);
                Assert.NotNull(execution.CompletedUtc);
                Assert.Null(execution.Error);
            }
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task ProcessDueRunsAsync_MarksFailedExecutionWhenOrchestrationFails()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: false);
            InitializeGitRepository(root);
            var orchestrationService = CreateOrchestrationService(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))));

            using var provider = CreateServiceProvider(root, orchestrationService);

            ScheduledAgentRun createdRun;
            using (var scope = provider.CreateScope())
            {
                var runService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
                createdRun = await runService.CreateAsync(new ScheduledAgentRun
                {
                    Name = "Nightly dispatcher failure",
                    Enabled = true,
                    CronExpression = "0 0 * * *",
                    TaskName = "Implement orchestration",
                    UserRequest = "Create: Models/OrchestrationExample.cs",
                    ExecutionPolicyName = "Safe",
                    CommitAndSync = true,
                    NextRunUtc = DateTime.UtcNow.AddMinutes(-5)
                });
            }

            var dispatcher = new ScheduledRunDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ScheduledRunDispatcher>.Instance);

            await InvokeProcessDueRunsAsync(dispatcher);

            using (var scope = provider.CreateScope())
            {
                var runService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
                var executionService = scope.ServiceProvider.GetRequiredService<ScheduledAgentExecutionService>();

                var updatedRun = await runService.GetByIdAsync(createdRun.Id);
                Assert.NotNull(updatedRun);
                Assert.False(updatedRun!.IsRunning);
                Assert.NotNull(updatedRun.LastStartedUtc);
                Assert.NotNull(updatedRun.LastFailedUtc);
                Assert.NotNull(updatedRun.LastError);
                Assert.NotEmpty(updatedRun.LastError);

                var executions = await executionService.GetByRunIdAsync(createdRun.Id);
                var execution = Assert.Single(executions);
                Assert.Equal(createdRun.Id, execution.ScheduledRunId);
                Assert.Equal(createdRun.Name, execution.ScheduleName);
                Assert.Equal(createdRun.TaskName, execution.TaskName);
                Assert.False(execution.Success);
                Assert.NotNull(execution.CompletedUtc);
                Assert.NotNull(execution.Error);
                Assert.NotEmpty(execution.Error);
            }
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static async Task InvokeProcessDueRunsAsync(ScheduledRunDispatcher dispatcher)
    {
        var method = typeof(ScheduledRunDispatcher).GetMethod("ProcessDueRunsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method!.Invoke(dispatcher, new object[] { CancellationToken.None });
        Assert.NotNull(task);
        await task!;
    }

    private static ServiceProvider CreateServiceProvider(string root, AgentOrchestrationService orchestrationService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(root));
        services.AddSingleton(orchestrationService);
        services.AddScoped<ExecutionPolicyProfileService>();
        services.AddScoped<ScheduledAgentRunService>();
        services.AddScoped<ScheduledAgentExecutionService>();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<ScheduledRunDispatcher>), NullLogger<ScheduledRunDispatcher>.Instance);
        return services.BuildServiceProvider();
    }

    private static AgentOrchestrationService CreateOrchestrationService(string root, params string[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                ["AiBox:LocalCoder:WorkspaceRoots:0"] = root
            })
            .Build();

        var environment = new TestWebHostEnvironment(root);
        var agentRunHistoryService = new AgentRunHistoryService(environment);
        var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
        var contextService = new LocalCoderContextService();
        var patchVerificationService = new PatchVerificationService(agentRunHistoryService);
        var repairService = new PatchPreviewRepairService(
            new OllamaService(httpClient),
            patchEditOperationService,
            configuration);
        var agentInstructionService = new AgentInstructionService(environment);
        var coderConsoleService = new CoderConsoleService(
            httpClient,
            configuration,
            environment,
            patchEditOperationService,
            contextService,
            patchVerificationService,
            new SelectedContextValidator(),
            repairService,
            agentInstructionService);
        var profileService = new AgentModeProfileService(environment);
        var patchPreviewPreparationService = new TaskSlicePatchPreviewPreparationService(contextService);
        var taskDecompositionService = new TaskDecompositionService(new RiskAnalysisService());
        var taskSliceVerificationService = new TaskSliceVerificationService(environment);
        var patchApprovalGateService = new PatchApprovalGateService();
        var patchPackageService = new PatchPackageService(environment, patchApprovalGateService);
        var patchRollbackService = new PatchRollbackService(patchPackageService, environment);
        var patchBackupService = new PatchBackupService(environment, NullLogger<PatchBackupService>.Instance);
        var taskSliceRollbackService = new TaskSliceRollbackService(patchRollbackService);
        var taskSliceApplyHistoryService = new TaskSliceApplyHistoryService(environment);
        var taskSliceApprovalService = new TaskSliceApprovalService(environment);
        var taskSliceApplyAuditService = new TaskSliceApplyAuditService(environment);
        var taskSliceApplyService = new TaskSliceApplyService(
            patchBackupService,
            patchPackageService,
            patchRollbackService,
            taskSliceRollbackService,
            taskSliceApplyHistoryService,
            taskSliceApprovalService,
            taskSliceApplyAuditService);
        var executionPolicyProfileService = new ExecutionPolicyProfileService(environment);
        var safetyService = new AgentOrchestrationSafetyService(environment, executionPolicyProfileService);
        var checkpointService = new AgentOrchestrationCheckpointService(environment);
        var timelineService = new AgentOrchestrationTimelineService(environment);
        var approvalQueueService = new HumanApprovalQueueService(environment, timelineService);
        var gitSyncService = new GitSyncService();
        var localCoderPatchService = new LocalCoderPatchService(
            new OllamaService(httpClient),
            new RepositoryFileContextService());
        var localCoderReviewService = new LocalCoderReviewService(new OllamaService(httpClient));

        return new AgentOrchestrationService(
            environment,
            taskDecompositionService,
            patchPreviewPreparationService,
            coderConsoleService,
            profileService,
            new AgentModelRouteService(environment),
            taskSliceVerificationService,
            localCoderPatchService,
            localCoderReviewService,
            patchPackageService,
            taskSliceApprovalService,
            taskSliceApplyAuditService,
            taskSliceApplyService,
            approvalQueueService,
            checkpointService,
            executionPolicyProfileService,
            safetyService,
            timelineService,
            gitSyncService);
    }

    private static string BuildPatchOperationsJson(params PatchOperationFixture[] operations)
    {
        return JsonSerializer.Serialize(new
        {
            operations = operations.Select(operation => new
            {
                filePath = operation.FilePath,
                operation = "create",
                newText = operation.NewText
            }).ToArray()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string BuildOllamaResponse(string responseText)
    {
        return JsonSerializer.Serialize(new
        {
            response = responseText,
            done = true,
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = responseText
                    }
                }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"scheduled-run-dispatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteProjectFile(string root, bool validProgram)
    {
        File.WriteAllText(
            Path.Combine(root, "OrchestrationTest.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        var entryPointPath = Path.Combine(root, "EntryPoint.cs");
        var entryPointText = validProgram
            ? """
              namespace AiBox.DevPortal.Tests;

              public static class EntryPoint
              {
                  public static void Main()
                  {
                      Console.WriteLine("Hello from orchestration tests");
                  }
              }
              """
            : """
              namespace AiBox.DevPortal.Tests;

              public static class EntryPoint
              {
                  public static void Main()
                  {
                      Console.WriteLine("Broken build"
                  }
              }
              """;
        File.WriteAllText(entryPointPath, entryPointText);
    }

    private static void InitializeGitRepository(string root)
    {
        var remoteRoot = Path.Combine(Path.GetTempPath(), $"scheduled-run-dispatcher-remote-{Guid.NewGuid():N}.git");
        RunGitCommand(Path.GetTempPath(), ["init", "--bare", remoteRoot]);

        RunGitCommand(root, ["init"]);
        RunGitCommand(root, ["checkout", "-b", "main"]);
        RunGitCommand(root, ["config", "user.email", "test@example.com"]);
        RunGitCommand(root, ["config", "user.name", "Test User"]);
        RunGitCommand(root, ["remote", "add", "origin", remoteRoot]);

        RunGitCommand(root, ["add", "-A"]);
        RunGitCommand(root, ["commit", "-m", "Initial baseline"]);
        RunGitCommand(root, ["push", "-u", "origin", "main"]);
    }

    private static void RunGitCommand(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git process.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed: git {string.Join(" ", arguments)}{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PatchOperationFixture(string FilePath, string NewText);

    private sealed class QueueHandler(string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No more queued responses are available.");
            }

            var payload = responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AiBox.DevPortal.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
