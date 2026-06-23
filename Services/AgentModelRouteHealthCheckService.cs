using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelRouteHealthCheckService(IHttpClientFactory httpClientFactory)
{
    public async Task<(bool IsHealthy, string Message)> CheckAsync(AgentModelRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        var healthEndpoint = GetHealthCheckEndpoint(route);
        var unreachableMessage = $"Model route '{route.Name}' is not reachable at {healthEndpoint}.";

        if (string.IsNullOrWhiteSpace(route.BaseUrl) || string.IsNullOrWhiteSpace(route.Model))
        {
            return (false, unreachableMessage);
        }

        if (!Uri.TryCreate(healthEndpoint, UriKind.Absolute, out var endpoint))
        {
            return (false, unreachableMessage);
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(AgentModelRouteHealthCheckService));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, $"Model route '{route.Name}' is healthy at {healthEndpoint}.");
            }

            return (false, unreachableMessage);
        }
        catch
        {
            return (false, unreachableMessage);
        }
    }

    private static string GetHealthCheckEndpoint(AgentModelRoute route)
    {
        var baseUrl = route.BaseUrl.Trim();

        if (baseUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl[..^"/v1/chat/completions".Length] + "/v1/models";
        }

        if (route.Provider.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl.TrimEnd('/') + "/api/tags";
        }

        return baseUrl;
    }
}
