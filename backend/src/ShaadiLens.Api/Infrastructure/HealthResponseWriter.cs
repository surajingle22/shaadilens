using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShaadiLens.Api.Infrastructure;

/// <summary>Writes health results as JSON: { status, totalMs, checks: { postgres: {...}, "face-service": {...} } }.</summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            totalMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    description = e.Value.Description,
                }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
