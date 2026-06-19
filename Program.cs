using AiBox.DevPortal.Api;
using AiBox.DevPortal.Components;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Services.Browser;
using AiBox.DevPortal.Services.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "Data", "DataProtection-Keys")))
    .SetApplicationName("AiBox.DevPortal");

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

builder.Services.AddRadzenComponents();
builder.Services.AddScoped<IClipboardService, ClipboardService>();
builder.Services.AddScoped<IAgentRegistryService, AgentRegistryService>();
builder.Services.AddScoped<IAgentModeProfileService, AgentModeProfileService>();
builder.Services.AddScoped<IAgentRunHistoryService, AgentRunHistoryService>();
builder.Services.AddScoped<IAgentModeRunner, AgentModeRunner>();
builder.Services.AddScoped<AgentInstructionService>();
builder.Services.AddScoped<PlannerContextSelectionService>();
builder.Services.AddScoped<TaskSlicePatchPreviewPreparationService>();
builder.Services.AddScoped<PatchVerificationService>();
builder.Services.AddScoped<SelectedContextValidator>();
builder.Services.AddScoped<PatchPreviewRepairService>();
builder.Services.AddScoped<IPatchApprovalGateService, PatchApprovalGateService>();
builder.Services.AddScoped<IPatchEditOperationService, PatchEditOperationService>();
builder.Services.AddScoped<IPatchPackageService, PatchPackageService>();
builder.Services.AddScoped<IPatchApplyService, PatchApplyService>();
builder.Services.AddScoped<IPatchRollbackService, PatchRollbackService>();
builder.Services.AddScoped<IPatchBackupService, PatchBackupService>();
builder.Services.AddScoped<ICoderConsoleService, CoderConsoleService>();
builder.Services.AddScoped<ILocalCoderContextService, LocalCoderContextService>();
builder.Services.AddScoped<ILocalCoderVerificationProfileService, LocalCoderVerificationProfileService>();
builder.Services.AddScoped<AiBox.DevPortal.Services.ILocalCoderHistoryService, AiBox.DevPortal.Services.LocalCoderHistoryService>();
builder.Services.AddScoped<AiBox.DevPortal.Services.Agents.ILocalCoderHistoryService, AiBox.DevPortal.Services.Agents.LocalCoderHistoryService>();
builder.Services.AddScoped<ILocalCoderMarkdownService, LocalCoderMarkdownService>();
builder.Services.AddScoped<ILocalCoderPatchService, LocalCoderPatchService>();
builder.Services.AddScoped<ILocalCoderBuildService, LocalCoderBuildService>();
builder.Services.AddScoped<ILocalCoderReviewService, LocalCoderReviewService>();
builder.Services.AddScoped<IRepositoryScannerService, RepositoryScannerService>();
builder.Services.AddScoped<IRepositoryFileContextService, RepositoryFileContextService>();
builder.Services.AddScoped<IExecutionPermissionProfileService, ExecutionPermissionProfileService>();
builder.Services.AddScoped<IExecutionEngineService, ExecutionEngineService>();
builder.Services.AddScoped<IFileOperationService, FileOperationService>();
builder.Services.AddScoped<IFileSearchService, FileSearchService>();
builder.Services.AddScoped<IGitOperationService, GitOperationService>();
builder.Services.AddScoped<IDockerOperationService, DockerOperationService>();
builder.Services.AddScoped<IComfyUiOperationService, ComfyUiOperationService>();
builder.Services.AddScoped<IVerificationService, VerificationService>();
builder.Services.AddScoped<IOrchestrationDashboardService, OrchestrationDashboardService>();
builder.Services.AddScoped<IProjectRegistryService, ProjectRegistryService>();
builder.Services.AddScoped<IProjectKnowledgeIndexService, ProjectKnowledgeIndexService>();
builder.Services.AddScoped<ProjectHistoryIndexService>();
builder.Services.AddScoped<TaskSliceExecutionService>();
builder.Services.AddScoped<TaskPlanApplyService>();
builder.Services.AddScoped<TaskPlanDependencyGraphService>();
builder.Services.AddScoped<TaskSliceApplyHistoryService>();
builder.Services.AddScoped<TaskSliceApprovalService>();
builder.Services.AddScoped<TaskSliceRiskAnalysisService>();
builder.Services.AddScoped<AgentDashboardMetricsService>();
builder.Services.AddScoped<TaskSliceApplyService>();
builder.Services.AddScoped<TaskSliceRollbackService>();
builder.Services.AddScoped<TaskSliceVerificationLoopService>();
builder.Services.AddScoped<IWorkflowRegistryService, WorkflowRegistryService>();
builder.Services.AddScoped<IWorkflowTemplateService, WorkflowTemplateService>();
builder.Services.AddScoped<IWorkflowRunPreviewService, WorkflowRunPreviewService>();
builder.Services.AddScoped<IWorkflowRunHistoryService, WorkflowRunHistoryService>();
builder.Services.AddScoped<TaskSliceVerificationService>();
builder.Services.AddScoped<IDockerService, DockerService>();
builder.Services.AddScoped<IGeneratedImageHistoryService, GeneratedImageHistoryService>();
builder.Services.AddScoped<IImageToolService, ImageToolService>();
builder.Services.AddHttpClient<IToolStatusService, ToolStatusService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient<IComfyUiService, ComfyUiService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration.GetComfyBaseUrl());
});
builder.Services.AddHttpClient<ISdxlTextToImageService, SdxlTextToImageService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration.GetComfyBaseUrl());
});
builder.Services.AddHttpClient<IOllamaService, OllamaService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration.GetOllamaBaseUrl());
});
builder.Services.AddHttpClient<ILocalLlmService, OllamaLocalLlmService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration.GetOllamaBaseUrl());
});
builder.Services.AddHttpClient<IPromptEnhancerService, PromptEnhancerService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration.GetOllamaBaseUrl());
});
builder.Services.AddHttpClient<
    ICoderConsoleService,
    CoderConsoleService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["AiBox:OllamaUrl"] ?? "http://localhost:11434");
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<ILocalCoderService, LocalCoderService>();
builder.Services.AddScoped<TaskDecompositionService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}


app.UseAntiforgery();

app.MapStaticAssets();
app.MapAgentEndpoints();
app.MapExecutionPermissionProfileEndpoints();
app.MapExecutionEndpoints();
app.MapFileEndpoints();
app.MapGitEndpoints();
app.MapDockerOperationEndpoints();
app.MapComfyUiOperationEndpoints();
app.MapOrchestrationEndpoints();
app.MapProjectEndpoints();
app.MapWorkflowEndpoints();
app.MapWorkflowTemplateEndpoints();
app.MapWorkflowRunEndpoints();
app.MapImageToolEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
