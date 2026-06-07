using AiBox.DevPortal.Api;
using AiBox.DevPortal.Components;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Http.Features;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

builder.Services.AddRadzenComponents();
builder.Services.AddScoped<IAgentRegistryService, AgentRegistryService>();
builder.Services.AddScoped<IExecutionPermissionProfileService, ExecutionPermissionProfileService>();
builder.Services.AddScoped<IExecutionEngineService, ExecutionEngineService>();
builder.Services.AddScoped<IFileOperationService, FileOperationService>();
builder.Services.AddScoped<IGitOperationService, GitOperationService>();
builder.Services.AddScoped<IDockerOperationService, DockerOperationService>();
builder.Services.AddScoped<IComfyUiOperationService, ComfyUiOperationService>();
builder.Services.AddScoped<IVerificationService, VerificationService>();
builder.Services.AddScoped<IOrchestrationDashboardService, OrchestrationDashboardService>();
builder.Services.AddScoped<IProjectRegistryService, ProjectRegistryService>();
builder.Services.AddScoped<IWorkflowRegistryService, WorkflowRegistryService>();
builder.Services.AddScoped<IWorkflowTemplateService, WorkflowTemplateService>();
builder.Services.AddScoped<IWorkflowRunPreviewService, WorkflowRunPreviewService>();
builder.Services.AddScoped<IWorkflowRunHistoryService, WorkflowRunHistoryService>();
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


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
