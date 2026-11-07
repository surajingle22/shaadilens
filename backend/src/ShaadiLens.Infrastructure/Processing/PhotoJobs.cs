using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Faces;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.FaceService;
using ShaadiLens.Infrastructure.Persistence;


namespace ShaadiLens.Infrastructure.Processing;

/// <summary>
/// The two background jobs every photo goes through (run by Hangfire, stored in Postgres):
///
///   PrepareAsync      queue "default"  EXIF + orientation + web/thumb     (~0.5–1 s, CPU)
///        ↓ enqueues
///   DetectFacesAsync  queue "faces"    face-service → Face rows in pgvector (~0.4 s + 0.4 s/face)
///
/// Two queues so thumbnails appear quickly even while face detection is busy, and so the number of
/// parallel face jobs (FACE_WORKERS) can be tuned separately.
///
/// Jobs are idempotent: running one twice gives the same result (faces are replaced, not added).
/// Hangfire retries a failing job up to 3 times; after that the photo is marked Failed.
/// </summary>
public sealed class PhotoJobs(
    AppDbContext db,
    ImageProcessor images,
    IFaceEngine faceEngine,
    IStorageService storage,
    IBackgroundJobClient jobs,
    ILogger<PhotoJobs> log)
{
    public const int MaxAttempts = 3;

    [Queue("default")]
    [AutomaticRetry(Attempts = MaxAttempts - 1, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task PrepareAsync(Guid photoId, PerformContext? context, CancellationToken ct)
    {
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo is null) return; // deleted meanwhile

        if (photo.Status < PhotoStatus.Resized || photo.StorageKeyWeb is null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await images.PrepareAsync(photo, ct);
            }
            catch (ImageMagick.MagickException ex)
            {
                // A broken/unsupported file won't get better by retrying.
                await MarkFailedAsync(photo, $"Unreadable image: {ex.Message}", ct);
                return;
            }
            catch (Exception ex) when (IsLastAttempt(context))
            {
                await MarkFailedAsync(photo, ex.Message, ct);
                return;
            }

            photo.Status = PhotoStatus.Resized;
            photo.Error = null;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Prepared photo {PhotoId} ({Width}x{Height}) in {Ms} ms",
                photoId, photo.Width, photo.Height, sw.ElapsedMilliseconds);
        }

        jobs.Enqueue<PhotoJobs>(j => j.DetectFacesAsync(photoId, null, CancellationToken.None));
    }

    [Queue("faces")]
    [AutomaticRetry(Attempts = MaxAttempts - 1, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task DetectFacesAsync(Guid photoId, PerformContext? context, CancellationToken ct)
    {
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo?.StorageKeyWeb is null) return;

        var settings = await db.Events.AsNoTracking().Where(e => e.Id == photo.EventId).Select(e => e.FaceSettings).FirstAsync(ct);

        FaceAnalysisResult result;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var image = await storage.OpenReadAsync(photo.StorageKeyWeb, ct);
            result = await faceEngine.AnalyzeAsync(image, "web.jpg", ct);
        }
        catch (FaceServiceException ex) when (ex.StatusCode is 400 or 413)
        {
            await MarkFailedAsync(photo, $"face-service rejected the image: {ex.Message}", ct);
            return;
        }
        catch (Exception ex) when (IsLastAttempt(context))
        {
            await MarkFailedAsync(photo, $"Face detection failed: {ex.Message}", ct);
            return;
        }

        // Replace any faces from a previous run (idempotent re-processing). Matches on them cascade.
        await db.Faces.Where(f => f.PhotoId == photoId).ExecuteDeleteAsync(ct);

        var kept = result.Faces
            .Where(f => f.FacePx >= settings.MinFaceSizePx && f.DetScore >= settings.MinDetScore)
            .Select(f => ToEntity(f, photo, result.Width, result.Height))
            .ToList();
        db.Faces.AddRange(kept);

        photo.FaceCount = kept.Count;
        photo.Status = PhotoStatus.FacesDetected;
        photo.ProcessedAt = DateTimeOffset.UtcNow;
        photo.Error = null;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Faces for photo {PhotoId}: {Kept}/{Found} kept in {Ms} ms (face-service {EngineMs} ms)",
            photoId, kept.Count, result.Faces.Count, sw.ElapsedMilliseconds, (int)result.ElapsedMs);
    }

    /// <summary>Stores the box normalised to 0..1 so it overlays correctly on thumb, web or original.</summary>
    private static Face ToEntity(DetectedFace f, Photo photo, int width, int height) => new()
    {
        PhotoId = photo.Id,
        EventId = photo.EventId,
        BoxX = f.BBox.X / width,
        BoxY = f.BBox.Y / height,
        BoxW = f.BBox.W / width,
        BoxH = f.BBox.H / height,
        LandmarksJson = JsonSerializer.Serialize(f.Kps.Select(p => new[] { p[0] / width, p[1] / height })),
        DetScore = f.DetScore,
        Embedding = new Vector(f.Embedding),
        QualityScore = f.Blur,
        FaceSizePx = f.FacePx,
        Yaw = f.Pose.Yaw,
        Pitch = f.Pose.Pitch,
    };

    private async Task MarkFailedAsync(Photo photo, string error, CancellationToken ct)
    {
        log.LogWarning("Photo {PhotoId} failed: {Error}", photo.Id, error);
        photo.Status = PhotoStatus.Failed;
        photo.Error = error.Length > 500 ? error[..500] : error;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hangfire stores how many times it already retried this job.</summary>
    private static bool IsLastAttempt(PerformContext? context) =>
        context is null || context.GetJobParameter<int>("RetryCount") >= MaxAttempts - 1;
}

/// <summary>IPhotoPipelineScheduler backed by Hangfire.</summary>
public sealed class HangfirePhotoPipelineScheduler(IBackgroundJobClient jobs) : IPhotoPipelineScheduler
{
    public void EnqueueProcessing(Guid photoId) =>
        jobs.Enqueue<PhotoJobs>(j => j.PrepareAsync(photoId, null, CancellationToken.None));

    public void EnqueueFaceDetection(Guid photoId) =>
        jobs.Enqueue<PhotoJobs>(j => j.DetectFacesAsync(photoId, null, CancellationToken.None));
}
