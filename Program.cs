using AiBox.DevPortal.Components;
using AiBox.DevPortal.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();
builder.Services.AddScoped<IDockerService, DockerService>();
builder.Services.AddScoped<IGeneratedImageHistoryService, GeneratedImageHistoryService>();
builder.Services.AddHttpClient<IToolStatusService, ToolStatusService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient<IComfyUiService, ComfyUiService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["AiBox:ComfyUI:BaseUrl"] ?? configuration["AiBox:ComfyUrl"] ?? "http://localhost:8188");
});
builder.Services.AddHttpClient<ISdxlTextToImageService, SdxlTextToImageService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["AiBox:ComfyUrl"] ?? "http://localhost:8188");
});
builder.Services.AddHttpClient<IOllamaService, OllamaService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["AiBox:OllamaUrl"] ?? "http://localhost:11434");
});
builder.Services.AddHttpClient<IPromptEnhancerService, PromptEnhancerService>((serviceProvider, client) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(configuration["AiBox:OllamaUrl"] ?? "http://localhost:11434");
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
