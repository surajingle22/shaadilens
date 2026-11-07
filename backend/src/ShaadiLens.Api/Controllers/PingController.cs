using Microsoft.AspNetCore.Mvc;

namespace ShaadiLens.Api.Controllers;

/// <summary>Smoke-test endpoint used by the Angular shell to show "API connected".</summary>
[ApiController]
[Route("api/v1/ping")]
public sealed class PingController(IWebHostEnvironment env) : ControllerBase
{
    [HttpGet]
    public ActionResult<PingResponse> Get() => new PingResponse(
        Name: "ShaadiLens API",
        Version: typeof(PingController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        Environment: env.EnvironmentName,
        ServerTimeUtc: DateTimeOffset.UtcNow);
}

public sealed record PingResponse(string Name, string Version, string Environment, DateTimeOffset ServerTimeUtc);
