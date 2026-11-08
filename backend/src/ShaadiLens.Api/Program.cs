using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;
using ShaadiLens.Api.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShaadiLens.Api.Infrastructure;
using ShaadiLens.Api.Realtime;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application;
using ShaadiLens.Infrastructure;
using ShaadiLens.Infrastructure.Persistence;

// Bootstrap logger: catches errors that happen before the host is fully built.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog = structured logs: `log.Information("Processed {PhotoId}", id)` keeps PhotoId as a
    // field, not just text. Config comes from the "Serilog" section of appsettings.json.
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services
        .AddControllers(o => o.Filters.Add<ValidationFilter>())
        // Enums as strings in JSON ("FacesDetected", not 2) — easier to read in Angular and Swagger.
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddProblemDetails();                       // errors as RFC 7807 JSON
    builder.Services.AddExceptionHandler<AppExceptionHandler>();
    builder.Services.AddExceptionHandler<FaceServiceExceptionHandler>();
    builder.Services.AddOpenApi();                              // generates /openapi/v1.json

    builder.Services.AddApplication(builder.Configuration);     // use cases + validators
    builder.Services.AddInfrastructure(builder.Configuration);  // EF Core, Hangfire, storage, face-service
    builder.Services.AddAppAuth(builder.Configuration);         // JWT (admin cookie / guest bearer)

    // Live updates (SignalR WebSockets at /hubs/events). Jobs send through IRealtimeNotifier.
    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeNotifier, SignalRRealtimeNotifier>());

    // We sit behind nginx (and Cloudflare Tunnel): trust X-Forwarded-For/Proto so we see the real
    // client IP (rate limits) and know the request was HTTPS (secure cookies).
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    var app = builder.Build();

    // Create/upgrade the database schema and seed the demo event.
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    }

    app.UseForwardedHeaders();
    // Request logging goes OUTSIDE the exception handler, so it logs the final status code: an expected
    // "selfie not usable" is logged as 422, not as a 500 with a stack trace.
    app.UseSerilogRequestLogging(o =>
        o.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500
            ? Serilog.Events.LogEventLevel.Error
            : http.Request.Path.StartsWithSegments("/media") || http.Request.Path.StartsWithSegments("/health")
                ? Serilog.Events.LogEventLevel.Verbose     // don't flood logs with thumbnail requests
                : Serilog.Events.LogEventLevel.Information);
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        // Swagger UI page at /swagger reading the built-in OpenAPI document.
        app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ShaadiLens API v1"));
    }

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();
    app.MapHub<EventsHub>(EventsHub.Path);

    // Hourly privacy clean-up of expired events (only where this process runs the job server).
    if (app.Configuration.GetValue("Processing:RunJobServer", true))
    {
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ShaadiLens.Infrastructure.Processing.PurgeJobs>(
            ShaadiLens.Infrastructure.Processing.PurgeJobs.RecurringJobId, j => j.PurgeExpiredAsync(CancellationToken.None), Cron.Hourly());
    }

    // Background job dashboard (queues, retries, failures) — admin only.
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireAdminAuthorizationFilter()],
        DashboardTitle = "ShaadiLens jobs",
        AppPath = "/admin",
    });

    // Two health endpoints:
    //   /health/live -> "is the process up?" (runs no checks). Used by Docker's healthcheck so the
    //                   container isn't restarted while face-service is still loading its model.
    //   /health      -> full readiness: Postgres + face-service, JSON details.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Lets WebApplicationFactory<Program> find this assembly in integration tests.
public partial class Program;
