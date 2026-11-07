using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Faces;
using ShaadiLens.Infrastructure.FaceService;

namespace ShaadiLens.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Tag for checks that must pass before the app is "ready" (DB, face-service).</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FaceServiceOptions>(config.GetSection(FaceServiceOptions.Section));

        // Typed client: whoever asks for IFaceEngine gets a FaceServiceClient with this HttpClient.
        services.AddHttpClient<IFaceEngine, FaceServiceClient>(ConfigureFaceServiceClient);
        services.AddHttpClient(FaceServiceHealthCheck.HttpClientName, (sp, http) =>
        {
            ConfigureFaceServiceClient(sp, http);
            http.Timeout = TimeSpan.FromSeconds(3);
        });

        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured");

        services.AddHealthChecks()
            .AddNpgSql(connectionString, name: "postgres", tags: [ReadyTag])
            .AddCheck<FaceServiceHealthCheck>("face-service", tags: [ReadyTag]);

        return services;
    }

    private static void ConfigureFaceServiceClient(IServiceProvider sp, HttpClient http)
    {
        var opts = sp.GetRequiredService<IOptions<FaceServiceOptions>>().Value;
        http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    }
}
