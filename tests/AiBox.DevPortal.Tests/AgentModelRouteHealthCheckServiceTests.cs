using System.Net;
using System.Net.Http;
using System.Text;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModelRouteHealthCheckServiceTests
{
    [Fact]
    public async Task CheckAsync_HealthyRoute_ReturnsHealthy()
    {
        var service = new AgentModelRouteHealthCheckService(new TestHttpClientFactory(HttpStatusCode.OK));
        var route = new AgentModelRoute
        {
            Name = "llama.cpp / DevPortal Local Coder",
            BaseUrl = "http://localhost:8082/v1/chat/completions",
            Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf"
        };

        var result = await service.CheckAsync(route);

        Assert.True(result.IsHealthy);
        Assert.Contains("healthy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_UnreachableRoute_ReturnsClearMessage()
    {
        var service = new AgentModelRouteHealthCheckService(new TestHttpClientFactory(HttpStatusCode.ServiceUnavailable));
        var route = new AgentModelRoute
        {
            Name = "llama.cpp / DevPortal Local Coder",
            BaseUrl = "http://localhost:8082/v1/chat/completions",
            Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf"
        };

        var result = await service.CheckAsync(route);

        Assert.False(result.IsHealthy);
        Assert.Equal("Model route 'llama.cpp / DevPortal Local Coder' is not reachable at http://localhost:8082/v1/chat/completions.", result.Message);
    }

    private sealed class TestHttpClientFactory(HttpStatusCode statusCode) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHandler(statusCode));
    }

    private sealed class TestHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
