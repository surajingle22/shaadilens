using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Faces;
using ShaadiLens.Application.Photos;
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
///                                      → reverse match against every registered guest (a few ms)
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
    IFaceMatcher matcher,
    IRealtimeNotifier realtime,
    IGuestNotifier notifier,
    IStorageService storage,
    IBackgroundJobClient jobs,
    BurstGrouper bursts,
    IOptions<CullingOptions> culling,
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
                await images.PrepareAsync(photo, await WatermarkAsync(photo.EventId, ct), ct);
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
            await realtime.PhotoProcessedAsync(photo.EventId, photoId, photo.Status, ct); // thumbnail is ready
        }

        jobs.Enqueue<PhotoJobs>(j => j.DetectFacesAsync(photoId, null, CancellationToken.None));
    }

    /// <summary>
    /// Re-renders web + thumb from the original (e.g. after turning the watermark on/off). Faces are not touched.
    /// Browsers that already cached the old image may show it for up to an hour (signed URLs rotate hourly).
    /// </summary>
    [Queue("default")]
    [AutomaticRetry(Attempts = MaxAttempts - 1, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task RebuildImagesAsync(Guid photoId, CancellationToken ct)
    {
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo?.StorageKeyWeb is null) return; // never processed (or deleted): the normal pipeline handles it
        await images.PrepareAsync(photo, await WatermarkAsync(photo.EventId, ct), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> WatermarkAsync(Guid eventId, CancellationToken ct) =>
        await db.Events.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => e.WatermarkEnabled ? e.WatermarkText : null).FirstOrDefaultAsync(ct);

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

        // Replace any faces from a previous run (idempotent re-processing). Matches on them cascade, so first
        // remember the guests' own decisions ("That's me" / "Not me") and restore them after matching.
        var decisions = await db.GuestMatches.AsNoTracking()
            .Where(m => m.PhotoId == photoId && (m.Status == MatchStatus.Confirmed || m.Status == MatchStatus.Rejected))
            .ToDictionaryAsync(m => m.GuestId, m => m.Status, ct);
        await db.Faces.Where(f => f.PhotoId == photoId).ExecuteDeleteAsync(ct);

        var kept = result.Faces
            .Where(f => f.FacePx >= settings.MinFaceSizePx && f.DetScore >= settings.MinDetScore)
            .Select(f => ToEntity(f, photo, result.Width, result.Height))
            .ToList();
        db.Faces.AddRange(kept);
        Culling.Apply(photo, kept, result, culling.Value); // blur / eyes closed / perceptual hash

        photo.FaceCount = kept.Count;
        photo.Status = PhotoStatus.FacesDetected;
        photo.ProcessedAt = DateTimeOffset.UtcNow;
        photo.Error = null;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Faces for photo {PhotoId}: {Kept}/{Found} kept in {Ms} ms (face-service {EngineMs} ms)",
            photoId, kept.Count, result.Faces.Count, sw.ElapsedMilliseconds, (int)result.ElapsedMs);

        await GroupBurstAsync(photoId, ct);

        // Reverse search: does anyone who already registered appear in this new photo?
        var newlyVisible = await matcher.MatchPhotoAsync(photoId, decisions, ct);

        // Tell open browsers (admin progress, Full-gallery viewers, guests' "new photos" banner) and queue
        // the debounced push/email for guests who aren't looking right now.
        await realtime.PhotoProcessedAsync(photo.EventId, photoId, photo.Status, ct);
        foreach (var guestId in newlyVisible) await realtime.NewMatchesAsync(guestId, 1, ct);
        try
        {
            await notifier.GuestsGotNewPhotosAsync(newlyVisible, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not schedule notifications for photo {PhotoId}", photoId); // the photo itself is fine
        }
    }

    /// <summary>
    /// Backfill for photos processed before the culling report existed: asks the face-service again but only
    /// updates the culling fields (sharpness, eyes, perceptual hash). Faces, guest matches and People groups stay
    /// exactly as they are — re-running full face detection would replace the faces.
    /// </summary>
    [Queue("faces")]
    [AutomaticRetry(Attempts = 1)]
    public async Task AnalyzeQualityAsync(Guid photoId, CancellationToken ct)
    {
        var photo = await db.Photos.Include(p => p.Faces).FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo?.StorageKeyWeb is null) return;

        FaceAnalysisResult result;
        await using (var image = await storage.OpenReadAsync(photo.StorageKeyWeb, ct))
            result = await faceEngine.AnalyzeAsync(image, "web.jpg", ct);

        // Same image + same model → the same faces come back. Pair them with the stored faces by box overlap.
        foreach (var face in photo.Faces)
        {
            var best = result.Faces
                .Select(d => (d, iou: Iou(face, d.BBox, result.Width, result.Height)))
                .MaxBy(x => x.iou);
            face.EyeAspectRatio = best.iou >= 0.5 ? best.d.Ear : null;
        }
        Culling.Apply(photo, photo.Faces, result, culling.Value);
        await db.SaveChangesAsync(ct);
        await GroupBurstAsync(photoId, ct);
    }

    private static double Iou(Face f, FaceBox b, int width, int height)
    {
        double bx = b.X / width, by = b.Y / height, bw = b.W / width, bh = b.H / height;
        var ix = Math.Max(0, Math.Min(f.BoxX + f.BoxW, bx + bw) - Math.Max(f.BoxX, bx));
        var iy = Math.Max(0, Math.Min(f.BoxY + f.BoxH, by + bh) - Math.Max(f.BoxY, by));
        var inter = ix * iy;
        var union = f.BoxW * f.BoxH + bw * bh - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Near-duplicate grouping is a nice-to-have: never fail (and retry) the whole photo because of it.</summary>
    private async Task GroupBurstAsync(Guid photoId, CancellationToken ct)
    {
        try
        {
            await bursts.AssignAsync(photoId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Burst grouping failed for photo {PhotoId}", photoId);
            db.ChangeTracker.Clear();
        }
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
        EyeAspectRatio = f.Ear,
    };

    private async Task MarkFailedAsync(Photo photo, string error, CancellationToken ct)
    {
        log.LogWarning("Photo {PhotoId} failed: {Error}", photo.Id, error);
        photo.Status = PhotoStatus.Failed;
        photo.Error = error.Length > 500 ? error[..500] : error;
        await db.SaveChangesAsync(ct);
        await realtime.PhotoProcessedAsync(photo.EventId, photo.Id, photo.Status, ct);
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

    public void EnqueueImageRebuild(Guid photoId) =>
        jobs.Enqueue<PhotoJobs>(j => j.RebuildImagesAsync(photoId, CancellationToken.None));

    public void EnqueueQualityAnalysis(Guid photoId) =>
        jobs.Enqueue<PhotoJobs>(j => j.AnalyzeQualityAsync(photoId, CancellationToken.None));

    public void EnqueueClustering(Guid eventId, float eps, int minSamples) =>
        jobs.Enqueue<ClusterJobs>(j => j.RunAsync(eventId, eps, minSamples, CancellationToken.None));

    public void EnqueueRematchEvent(Guid eventId) =>
        jobs.Enqueue<MatchJobs>(j => j.RematchEventAsync(eventId, CancellationToken.None));
}

/// <summary>Event-wide matching jobs.</summary>
public sealed class MatchJobs(AppDbContext db, IFaceMatcher matcher, ILogger<MatchJobs> log)
{
    /// <summary>
    /// Searches again for every guest of the event, e.g. after the admin changed the thresholds.
    /// (Guests' own Confirmed/Rejected decisions are kept by the matcher.)
    /// </summary>
    [Queue("default")]
    [AutomaticRetry(Attempts = 1)]
    public async Task RematchEventAsync(Guid eventId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var guestIds = await db.Guests.AsNoTracking()
            .Where(g => g.EventId == eventId && g.References.Any())
            .Select(g => g.Id).ToListAsync(ct);
        foreach (var guestId in guestIds)
        {
            await matcher.SearchForGuestAsync(guestId, ct);
            db.ChangeTracker.Clear(); // keep memory flat over hundreds of guests
        }
        log.LogInformation("Re-matched {Guests} guests of event {EventId} in {Ms} ms", guestIds.Count, eventId, sw.ElapsedMilliseconds);
    }
}
