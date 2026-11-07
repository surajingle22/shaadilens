using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using ShaadiLens.Api.Infrastructure;
using ShaadiLens.Infrastructure;

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

    builder.Services.AddControllers();
    builder.Services.AddProblemDetails();                       // errors as RFC 7807 JSON
    builder.Services.AddExceptionHandler<FaceServiceExceptionHandler>();
    builder.Services.AddOpenApi();                              // generates /openapi/v1.json
    builder.Services.AddInfrastructure(builder.Configuration);  // face-service client, health checks

    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        // Swagger UI page at /swagger reading the built-in OpenAPI document.
        app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ShaadiLens API v1"));
    }

    app.MapControllers();

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
}
finally
{
    Log.CloseAndFlush();
}

// Lets WebApplicationFactory<Program> find this assembly in integration tests.
public partial class Program;
