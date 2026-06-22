using System.Net.Http.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelRouteHealthCheckService(IHttpClientFactory httpClientFactory)
{
    public async Task<(bool IsHealthy, string Message)> CheckAsync(AgentModelRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        var unreachableMessage = $"Model route '{route.Name}' is not reachable at {route.BaseUrl}.";

        if (string.IsNullOrWhiteSpace(route.BaseUrl) || string.IsNullOrWhiteSpace(route.Model))
        {
            return (false, unreachableMessage);
        }

        if (!Uri.TryCreate(route.BaseUrl, UriKind.Absolute, out var endpoint))
        {
            return (false, unreachableMessage);
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(AgentModelRouteHealthCheckService));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = route.Model,
                    messages = new[]
                    {
                        new { role = "user", content = "ping" }
                    },
                    max_tokens = 1,
                    stream = false
                })
            };

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, $"Model route '{route.Name}' is healthy at {route.BaseUrl}.");
            }

            return (false, unreachableMessage);
        }
        catch
        {
            return (false, unreachableMessage);
        }
    }
}
