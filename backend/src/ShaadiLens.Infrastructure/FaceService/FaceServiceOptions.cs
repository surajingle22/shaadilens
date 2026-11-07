namespace ShaadiLens.Infrastructure.FaceService;

/// <summary>Bound from the "FaceService" config section (env: FaceService__BaseUrl).</summary>
public sealed class FaceServiceOptions
{
    public const string Section = "FaceService";

    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Face analysis on CPU is ~0.3–1 s per photo, but allow for slow machines and queues.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}
