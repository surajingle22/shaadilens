using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Faces;
using Testcontainers.PostgreSql;

namespace ShaadiLens.Tests.Support;

/// <summary>One real Postgres + pgvector container shared by all DB tests (started once per test run).</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Db { get; } = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .Build();

    public Task InitializeAsync() => Db.StartAsync();
    public Task DisposeAsync() => Db.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "db";
}

/// <summary>
/// The real API in memory, pointed at the test Postgres. Swapped out:
///   - IFaceEngine → FakeFaceEngine (no Python needed; tests decide which faces exist)
///   - IPhotoPipelineScheduler → RecordingScheduler (tests run jobs explicitly, step by step)
///   - Hangfire workers off
/// </summary>
public sealed class ApiFactory(PostgresFixture pg) : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "correct horse battery staple";

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "shaadilens-tests", Guid.NewGuid().ToString("N"));
    public FakeFaceEngine Faces { get; } = new();
    public RecordingScheduler Scheduler { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", pg.Db.GetConnectionString());
        builder.UseSetting("Processing:RunJobServer", "false");
        builder.UseSetting("Storage:Root", StorageRoot);
        builder.UseSetting("Admin:Email", AdminEmail);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Jwt:Secret", "test-jwt-secret-0123456789-0123456789-abcdef");
        builder.UseSetting("Media:SigningKey", "test-media-key-0123456789-0123456789-abcdef");
        builder.UseSetting("Uploads:ChunkSizeBytes", (64 * 1024).ToString()); // small chunks → multi-chunk uploads in tests

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFaceEngine>();
            s.AddSingleton<IFaceEngine>(Faces);
            s.RemoveAll<IPhotoPipelineScheduler>();
            s.AddSingleton<IPhotoPipelineScheduler>(Scheduler);
        });
    }

    /// <summary>HttpClient logged in as admin (the auth cookie is kept by the client's cookie container).</summary>
    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = AdminEmail, password = AdminPassword });
        res.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(StorageRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeFaceEngine : IFaceEngine
{
    /// <summary>Faces to "detect" in the next analyzed image.</summary>
    public List<DetectedFace> NextFaces { get; set; } = [];
    public int Calls { get; private set; }

    public Task<FaceAnalysisResult> AnalyzeAsync(Stream image, string fileName, CancellationToken ct = default)
    {
        Calls++;
        // The pipeline sends the 2048px web image; pretend it's 2048x1365.
        return Task.FromResult(new FaceAnalysisResult(2048, 1365, NextFaces, 12.3));
    }

    /// <summary>What the next selfie check returns (set by tests).</summary>
    public SelfieCheck NextSelfie { get; set; } = new(false, "no_face", "No face", null);

    public Task<SelfieCheck> CheckSelfieAsync(Stream image, float maxYaw, CancellationToken ct = default) =>
        Task.FromResult(NextSelfie);

    public static SelfieCheck GoodSelfie(float[] embedding) => new(true, "ok", null, embedding);

    public Task<FaceEngineInfo> GetInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new FaceEngineInfo("fake", "0", "0", ["cpu"], 640, 1600, 512));

    /// <summary>
    /// Unit vector whose cosine similarity with axis <paramref name="axis"/> is exactly <paramref name="similarity"/>
    /// (the rest points along <paramref name="otherAxis"/>). Lets tests place faces precisely around the thresholds.
    /// </summary>
    public static float[] WithSimilarity(int axis, float similarity, int otherAxis)
    {
        var v = new float[512];
        v[axis] = similarity;
        v[otherAxis] = MathF.Sqrt(1 - similarity * similarity);
        return v;
    }

    public static DetectedFace FaceWith(float[] embedding, float x = 100) =>
        Face(0, x: x) with { Embedding = embedding };

    /// <summary>A face with a deterministic unit-length embedding pointing mostly along axis <paramref name="axis"/>.</summary>
    public static DetectedFace Face(int axis, float sizePx = 120, float detScore = 0.9f, float x = 100, float y = 100) =>
        new(new FaceBox(x, y, sizePx, sizePx * 1.2f),
            [[x + 30, y + 40], [x + 90, y + 40], [x + 60, y + 70], [x + 35, y + 100], [x + 85, y + 100]],
            detScore, UnitVector(axis), new FacePose(5, 2, 0), sizePx, 150);

    public static float[] UnitVector(int axis, float mix = 0f, int mixAxis = 0)
    {
        var v = new float[512];
        v[axis] = 1f;
        if (mix > 0) v[mixAxis] += mix;
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }
}

public sealed class RecordingScheduler : IPhotoPipelineScheduler
{
    public List<Guid> Processing { get; } = [];
    public List<Guid> FaceDetection { get; } = [];
    public void EnqueueProcessing(Guid photoId) => Processing.Add(photoId);
    public void EnqueueFaceDetection(Guid photoId) => FaceDetection.Add(photoId);
    public List<Guid> Rematch { get; } = [];
    public void EnqueueRematchEvent(Guid eventId) => Rematch.Add(eventId);
}
