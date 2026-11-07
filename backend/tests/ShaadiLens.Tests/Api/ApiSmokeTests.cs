using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ShaadiLens.Api.Controllers;

namespace ShaadiLens.Tests.Api;

/// <summary>
/// Boots the real API in memory (WebApplicationFactory). No Postgres or face-service needed:
/// these endpoints don't touch them. DB-backed tests with Testcontainers arrive in Phase 1.
/// </summary>
public sealed class ApiSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Ping_returns_api_name()
    {
        var client = factory.CreateClient();

        var ping = await client.GetFromJsonAsync<PingResponse>("/api/v1/ping");

        ping!.Name.Should().Be("ShaadiLens API");
    }

    [Fact]
    public async Task Liveness_is_healthy_without_dependencies()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
