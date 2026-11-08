using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Faces;
using ShaadiLens.Infrastructure.FaceService;
using ShaadiLens.Infrastructure.Matching;
using ShaadiLens.Infrastructure.Media;
using ShaadiLens.Infrastructure.Notifications;
using ShaadiLens.Infrastructure.Sharing;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Infrastructure.Storage;

namespace ShaadiLens.Infrastructure;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";
    /// <summary>Run Hangfire workers in this process (false in tests).</summary>
    public bool RunJobServer { get; set; } = true;
    /// <summary>Parallel resize jobs. Each holds a ~2048px image in memory (~15 MB).</summary>
    public int ImageWorkers { get; set; } = 2;
    /// <summary>Parallel face-detection jobs (env FACE_WORKERS). Spec default: CPU cores − 1.</summary>
    public int FaceWorkers { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
}

public static class DependencyInjection
{
    /// <summary>Tag for checks that must pass before the app is "ready" (DB, face-service).</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured");

        services.Configure<FaceServiceOptions>(config.GetSection(FaceServiceOptions.Section));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.Configure<MediaOptions>(config.GetSection(MediaOptions.Section));
        services.Configure<ProcessingOptions>(config.GetSection(ProcessingOptions.Section));
        services.AddSingleton(TimeProvider.System);

        // ---- Database: EF Core + Npgsql + pgvector ----
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<DatabaseInitializer>();

        // ---- Storage + signed media URLs ----
        services.AddSingleton<LocalDiskStorage>();
        services.AddSingleton<IStorageService>(sp => sp.GetRequiredService<LocalDiskStorage>());
        services.AddSingleton<IMediaUrlSigner, MediaUrlSigner>();

        // ---- Sharing: face matching, selfie normalisation, QR + posters ----
        services.AddScoped<IFaceMatcher, PgvectorFaceMatcher>();
        services.AddSingleton<IImageTools, MagickImageTools>();
        services.AddSingleton<IShareArtwork, ShareArtwork>();

        // ---- face-service client ----
        services.AddHttpClient<IFaceEngine, FaceServiceClient>(ConfigureFaceServiceClient);
        services.AddHttpClient(FaceServiceHealthCheck.HttpClientName, (sp, http) =>
        {
            ConfigureFaceServiceClient(sp, http);
            http.Timeout = TimeSpan.FromSeconds(3);
        });

        // ---- Notifications: Web Push + email (both optional, configured in .env) ----
        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
        services.Configure<PushOptions>(config.GetSection(PushOptions.Section));
        services.AddHttpClient(nameof(WebPushSender), http => http.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddScoped<NotifyJobs>();
        services.AddScoped<IGuestNotifier>(sp => sp.GetRequiredService<NotifyJobs>());
        // The Api project replaces this with the SignalR implementation.
        services.TryAddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();

        // ---- Background jobs (Hangfire, stored in Postgres schema "hangfire") ----
        services.AddScoped<ImageProcessor>();
        services.AddScoped<PhotoJobs>();
        services.AddScoped<MatchJobs>();
        services.AddScoped<PurgeJobs>();
        services.AddScoped<ClusterJobs>();
        services.AddScoped<IPhotoPipelineScheduler, HangfirePhotoPipelineScheduler>();
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            // Poll for new jobs every second (default 15 s) so thumbnails appear within ~2 s of upload.
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions { QueuePollInterval = TimeSpan.FromSeconds(1) }));

        var processing = config.GetSection(ProcessingOptions.Section).Get<ProcessingOptions>() ?? new ProcessingOptions();
        if (processing.RunJobServer)
        {
            // Two Hangfire "servers" in this process = two worker pools with their own sizes.
            services.AddHangfireServer(o =>
            {
                o.ServerName = "images";
                o.Queues = ["default"];
                o.WorkerCount = processing.ImageWorkers;
            });
            services.AddHangfireServer(o =>
            {
                o.ServerName = "faces";
                o.Queues = ["faces"];
                o.WorkerCount = processing.FaceWorkers;
            });
        }

        // ---- Health checks ----
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
