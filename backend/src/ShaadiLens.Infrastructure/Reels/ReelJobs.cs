using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Reels;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Storage;

namespace ShaadiLens.Infrastructure.Reels;

/// <summary>
/// Reel rendering. Runs on the Hangfire queue "video", which only the reel-worker container listens to — video
/// encoding is CPU-heavy (≈1 minute per reel) and must not slow down uploads or face matching in the api.
///
/// The worker can't talk to browsers (SignalR lives in the api), so when a reel changes state it queues a tiny
/// "announce" job on the "default" queue, which the api picks up and forwards to the guest's open page.
/// </summary>
public sealed class ReelJobs(
    AppDbContext db,
    LocalDiskStorage storage,
    IReelRenderer renderer,
    MusicLibrary music,
    IRealtimeNotifier realtime,
    IBackgroundJobClient jobs,
    IOptions<ReelOptions> options,
    TimeProvider time,
    ILogger<ReelJobs> log)
{
    [Queue("video")]
    [AutomaticRetry(Attempts = 0)] // a failed render is shown to the guest, who can simply try again
    public async Task RenderAsync(Guid reelId, CancellationToken ct)
    {
        var reel = await db.Reels.FirstOrDefaultAsync(r => r.Id == reelId, ct);
        if (reel is null || reel.Status is not (ReelStatus.Queued or ReelStatus.Rendering)) return;

        reel.Status = ReelStatus.Rendering;
        reel.StartedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        Announce(reel.Id);

        var sw = Stopwatch.StartNew();
        try
        {
            var request = await BuildRequestAsync(reel, ct);
            await renderer.RenderAsync(request, ct);

            reel.StorageKey = StorageKeys.Reel(reel.EventId, reel.GuestId, reel.Id);
            reel.SizeBytes = new FileInfo(request.OutputPath).Length;
            reel.Status = ReelStatus.Ready;
            reel.Error = null;
            reel.FinishedAt = time.GetUtcNow();
            await ReplaceOlderAsync(reel, ct);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Reel {ReelId}: {Photos} photos, {Mb:0.0} MB in {Seconds:0.0} s",
                reel.Id, request.Shots.Count, reel.SizeBytes / 1048576.0, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Reel {ReelId} failed", reel.Id);
            reel.Status = ReelStatus.Failed;
            reel.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            reel.FinishedAt = time.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
        }
        Announce(reel.Id);
    }

    /// <summary>Runs in the api (queue "default"): tell the guest's open page that their reel changed.</summary>
    [Queue("default")]
    [AutomaticRetry(Attempts = 0)]
    public async Task AnnounceAsync(Guid reelId, CancellationToken ct)
    {
        var reel = await db.Reels.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reelId, ct);
        if (reel is not null) await realtime.ReelUpdatedAsync(reel.GuestId, reel.Id, reel.Status, ct);
    }

    private void Announce(Guid reelId) => jobs.Enqueue<ReelJobs>(j => j.AnnounceAsync(reelId, CancellationToken.None));

    private async Task<ReelRenderRequest> BuildRequestAsync(Reel reel, CancellationToken ct)
    {
        var shots = JsonSerializer.Deserialize<List<ReelShot>>(reel.PlanJson) ?? [];
        var ids = shots.Select(s => s.PhotoId).ToList();
        var photos = await db.Photos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.StorageKeyOriginal, p.StorageKeyWeb }).ToDictionaryAsync(p => p.Id, ct);

        // Prefer the full-resolution original (sharper crop); fall back to the 2048px web image.
        var sources = new List<ReelSource>();
        foreach (var s in shots)
        {
            if (!photos.TryGetValue(s.PhotoId, out var p)) continue; // deleted since the reel was requested
            var path = new[] { p.StorageKeyOriginal, p.StorageKeyWeb }
                .Where(k => !string.IsNullOrEmpty(k)).Select(k => storage.GetPath(k!)).FirstOrDefault(File.Exists);
            if (path is not null) sources.Add(new ReelSource(path, s.FocusX, s.FocusY));
        }
        if (sources.Count < 2) throw new InvalidOperationException("Not enough photos left for a reel");

        var ev = await db.Events.AsNoTracking().FirstAsync(e => e.Id == reel.EventId, ct);
        var guestName = await db.Guests.AsNoTracking().Where(g => g.Id == reel.GuestId).Select(g => g.Name).FirstOrDefaultAsync(ct);
        var o = options.Value;
        return new ReelRenderRequest(
            sources,
            Title: ev.Name,
            Subtitle: string.IsNullOrWhiteSpace(guestName) ? "Highlights" : $"{guestName.Trim()}'s highlights",
            Watermark: ev.WatermarkEnabled && !string.IsNullOrWhiteSpace(ev.WatermarkText) ? ev.WatermarkText : ev.Name,
            MusicPath: music.PickRandom(),
            OutputPath: storage.GetPath(StorageKeys.Reel(reel.EventId, reel.GuestId, reel.Id)),
            SecondsPerPhoto: o.SecondsPerPhoto,
            Crossfade: o.CrossfadeSeconds);
    }

    /// <summary>One reel per guest on disk: the new one replaces older ones (files deleted, rows kept for the daily limit).</summary>
    private async Task ReplaceOlderAsync(Reel current, CancellationToken ct)
    {
        var older = await db.Reels.Where(r => r.GuestId == current.GuestId && r.Id != current.Id
            && r.Status == ReelStatus.Ready).ToListAsync(ct);
        foreach (var r in older)
        {
            if (r.StorageKey is { } key) await storage.DeleteAsync(key, ct);
            r.StorageKey = null;
            r.Status = ReelStatus.Replaced;
        }
    }
}
