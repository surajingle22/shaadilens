using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Reels;

public sealed class ReelOptions
{
    public const string Section = "Reels";
    /// <summary>Royalty-free tracks (.mp3/.m4a/.aac/.ogg/.wav) — one is picked at random per reel. Empty = silent.</summary>
    public string MusicFolder { get; set; } = "/data/music";
    /// <summary>Reels a guest can make per 24 hours (each one costs ~1 minute of CPU).</summary>
    public int MaxPerDay { get; set; } = 3;
    public int MinPhotos { get; set; } = 3;
    /// <summary>Each photo is on screen this long (the cross-fade overlaps the next one).</summary>
    public double SecondsPerPhoto { get; set; } = 3.0;
    public double CrossfadeSeconds { get; set; } = 0.6;
}

/// <param name="Url">Signed URL to play the MP4 (Ready only).</param>
/// <param name="DownloadUrl">Same file, sent as an attachment.</param>
public sealed record ReelDto(Guid Id, ReelStatus Status, int PhotoCount, float DurationSeconds, long SizeBytes,
    string? Url, string? DownloadUrl, DateTimeOffset CreatedAt, string? Error);

/// <param name="Available">The guest may make a reel right now.</param>
/// <param name="Reason">Why not (shown to the guest), when not available.</param>
public sealed record ReelStateDto(bool Enabled, bool Available, string? Reason, int PhotosAvailable, int RemainingToday, ReelDto? Latest);

public sealed record ReelSettingsDto(bool Enabled, int PhotoCount);

/// <summary>Guest side of highlight reels: state, "Make my reel".</summary>
public sealed class ReelService(
    IAppDbContext db, GuestAccessService guests, IPhotoPipelineScheduler jobs, IMediaUrlSigner urls,
    IOptions<ReelOptions> options, TimeProvider time)
{
    private static readonly MatchStatus[] Shown = [MatchStatus.Auto, MatchStatus.Confirmed];

    public async Task<ReelStateDto> GetAsync(GuestContext ctx, CancellationToken ct)
    {
        var (_, ev, _) = await guests.ValidateSessionAsync(ctx, ct);
        if (!ctx.LinkType.NeedsSelfie() || ctx.GuestId is not { } guestId)
            return new ReelStateDto(ev.ReelsEnabled, false, "Reels are made from your own photos.", 0, 0, null);

        var latest = await db.Reels.AsNoTracking().Where(r => r.GuestId == guestId && r.Status != ReelStatus.Replaced)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var photos = await Candidates(ctx.EventId, guestId).CountAsync(ct);
        var remaining = Math.Max(0, options.Value.MaxPerDay - await CreatedTodayAsync(guestId, ct));
        var reason = WhyNot(ev, photos, remaining, latest);
        return new ReelStateDto(ev.ReelsEnabled, reason is null, reason, photos, remaining, latest is null ? null : ToDto(latest));
    }

    /// <summary>"Make my reel": pick the photos now, render in the background (reel worker).</summary>
    public async Task<ReelDto> CreateAsync(GuestContext ctx, CancellationToken ct)
    {
        var (_, ev, _) = await guests.ValidateSessionAsync(ctx, ct);
        if (!ctx.LinkType.NeedsSelfie()) throw new ForbiddenException("Reels are made from your own photos (Guest links with a selfie).");
        var guestId = ctx.GuestId ?? throw new ForbiddenException("Reels are made from your own photos — take a selfie first.");

        var latest = await db.Reels.AsNoTracking().Where(r => r.GuestId == guestId && r.Status != ReelStatus.Replaced)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var candidates = await Candidates(ctx.EventId, guestId).ToListAsync(ct);
        var remaining = options.Value.MaxPerDay - await CreatedTodayAsync(guestId, ct);
        if (WhyNot(ev, candidates.Count, remaining, latest) is { } reason)
        {
            if (latest?.Status is ReelStatus.Queued or ReelStatus.Rendering) throw new ConflictException(reason);
            if (!ev.ReelsEnabled) throw new ForbiddenException(reason);
            throw new BadRequestException(reason);
        }

        var shots = ReelPlanner.Pick(candidates, Math.Clamp(ev.ReelPhotoCount, options.Value.MinPhotos, 30));
        var o = options.Value;
        var reel = new Reel
        {
            EventId = ctx.EventId,
            GuestId = guestId,
            PlanJson = JsonSerializer.Serialize(shots),
            PhotoCount = shots.Count,
            DurationSeconds = (float)Math.Round(shots.Count * o.SecondsPerPhoto - (shots.Count - 1) * o.CrossfadeSeconds, 1),
            CreatedAt = time.GetUtcNow(),
        };
        db.Reels.Add(reel);
        await db.SaveChangesAsync(ct);
        jobs.EnqueueReel(reel.Id);
        return ToDto(reel);
    }

    private string? WhyNot(Event ev, int photos, int remainingToday, Reel? latest)
    {
        if (!ev.ReelsEnabled) return "Reels are turned off for this event.";
        if (latest?.Status is ReelStatus.Queued or ReelStatus.Rendering) return "Your reel is being made — it takes about 2 minutes.";
        if (photos < options.Value.MinPhotos) return $"A reel needs at least {options.Value.MinPhotos} photos of you. Check back after more photos are uploaded.";
        if (remainingToday <= 0) return "You've made the maximum number of reels for today. Try again tomorrow.";
        return null;
    }

    private Task<int> CreatedTodayAsync(Guid guestId, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-1);
        return db.Reels.CountAsync(r => r.GuestId == guestId && r.CreatedAt > since && r.Status != ReelStatus.Failed, ct);
    }

    /// <summary>The guest's visible "Your photos", with the face that matched them (for the crop and the score).</summary>
    private IQueryable<ReelCandidate> Candidates(Guid eventId, Guid guestId) =>
        from m in db.GuestMatches.AsNoTracking()
        where m.GuestId == guestId && Shown.Contains(m.Status)
        let p = m.Photo!
        where p.EventId == eventId && !p.IsHidden && !p.Collection!.IsHidden && p.StorageKeyWeb != null
        join f in db.Faces.AsNoTracking() on m.FaceId equals f.Id
        select new ReelCandidate(
            p.Id, p.CollectionId, p.TakenAt ?? p.UploadedAt,
            f.BoxX + f.BoxW / 2, f.BoxY + f.BoxH / 2, f.FaceSizePx, f.QualityScore, f.EyeAspectRatio, f.Yaw,
            m.Status == MatchStatus.Confirmed, p.IsHighlight, p.BurstGroupId);

    private ReelDto ToDto(Reel r)
    {
        var ready = r.Status == ReelStatus.Ready;
        var url = ready ? urls.GetUrl(r.Id, PhotoVariant.Reel) : null;
        return new ReelDto(r.Id, r.Status, r.PhotoCount, r.DurationSeconds, r.SizeBytes,
            url, url is null ? null : url + "&download=1", r.CreatedAt,
            r.Status == ReelStatus.Failed ? "Sorry, making the reel failed. Please try again." : null);
    }
}
