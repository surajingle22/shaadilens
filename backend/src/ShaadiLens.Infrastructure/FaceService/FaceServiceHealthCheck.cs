using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShaadiLens.Infrastructure.FaceService;

/// <summary>Reports Healthy when face-service answers GET /health (i.e. the model is loaded).</summary>
public sealed class FaceServiceHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public const string HttpClientName = "face-service-health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync("health", ct);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"face-service returned {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Normal for the first ~30-60 s after `docker compose up` while the model loads.
            return HealthCheckResult.Unhealthy("face-service unreachable (still loading the model?)", ex);
        }
    }
}
