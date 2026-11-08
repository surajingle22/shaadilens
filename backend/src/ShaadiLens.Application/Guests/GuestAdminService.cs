using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Guests;

public sealed record GuestAdminDto(
    Guid Id, string? Name, string? Phone, string? Email, Guid ShareLinkId, bool HasSelfie,
    int MatchCount, int ProbableCount, int ConfirmedCount, int RejectedCount, int ReferenceCount,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSearchedAt, bool PushOn, bool EmailOn, DateTimeOffset? LastNotifiedAt);

/// <summary>Face box normalised 0..1 (multiply by the image size).</summary>
public sealed record FaceBoxDto(float X, float Y, float W, float H);

public sealed record MatchReviewDto(
    Guid Id, Guid PhotoId, string ThumbUrl, string WebUrl, int Width, int Height, FaceBoxDto Face,
    float Score, MatchStatus Status, DateTimeOffset CreatedAt);

/// <param name="Selfies">Reference ids that have an image (the guest's selfies).</param>
public sealed record GuestReviewDto(GuestAdminDto Guest, IReadOnlyList<Guid> Selfies, IReadOnlyList<MatchReviewDto> Matches);

public sealed record SetMatchStatusRequest(MatchStatus Status);

/// <summary>Admin view of registered guests + Match Review.</summary>
public sealed class GuestAdminService(
    IAppDbContext db, IStorageService storage, IFaceMatcher matcher, MatchFeedbackService feedback, IMediaUrlSigner urls,
    IGuestNotifier notifier)
{
    /// <summary>Enough for any one guest at a wedding; keeps the review page fast.</summary>
    public const int MaxReviewMatches = 1000;

    public async Task<IReadOnlyList<GuestAdminDto>> ListAsync(Guid eventId, CancellationToken ct) =>
        await Summaries(db.Guests.Where(g => g.EventId == eventId)).ToListAsync(ct);

    /// <summary>One guest's selfies and every match (with the matched face's box), best first per status.</summary>
    public async Task<GuestReviewDto> ReviewAsync(Guid guestId, CancellationToken ct)
    {
        var guest = await Summaries(db.Guests.Where(g => g.Id == guestId)).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Guest");
        var selfies = await db.GuestReferences.AsNoTracking()
            .Where(r => r.GuestId == guestId && r.ImageKey != null)
            .OrderBy(r => r.CreatedAt).Select(r => r.Id).ToListAsync(ct);

        var rows = await db.GuestMatches.AsNoTracking()
            .Where(m => m.GuestId == guestId)
            .OrderBy(m => m.Status == MatchStatus.Probable ? 0 : m.Status == MatchStatus.Auto ? 1 : m.Status == MatchStatus.Confirmed ? 2 : 3)
            .ThenByDescending(m => m.Score)
            .Take(MaxReviewMatches)
            .Join(db.Faces, m => m.FaceId, f => f.Id, (m, f) => new
            {
                m.Id, m.PhotoId, m.Score, m.Status, m.CreatedAt, m.Photo!.Width, m.Photo.Height,
                f.BoxX, f.BoxY, f.BoxW, f.BoxH,
            })
            .ToListAsync(ct);

        var matches = rows.Select(r => new MatchReviewDto(
            r.Id, r.PhotoId, urls.GetUrl(r.PhotoId, PhotoVariant.Thumb), urls.GetUrl(r.PhotoId, PhotoVariant.Web),
            r.Width, r.Height, new FaceBoxDto(r.BoxX, r.BoxY, r.BoxW, r.BoxH), r.Score, r.Status, r.CreatedAt)).ToList();
        return new GuestReviewDto(guest, selfies, matches);
    }

    /// <summary>Admin approves (Confirmed) or rejects a match — same effect as the guest's buttons.</summary>
    public async Task SetMatchStatusAsync(Guid matchId, MatchStatus status, CancellationToken ct)
    {
        var match = await db.GuestMatches.FirstOrDefaultAsync(m => m.Id == matchId, ct) ?? throw new NotFoundException("Match");
        await feedback.SetDecisionAsync(match, status, "admin", ct);
    }

    /// <summary>The selfie image behind a reference (admin only; never exposed to other guests).</summary>
    public async Task<Stream> OpenSelfieAsync(Guid referenceId, CancellationToken ct)
    {
        var key = await db.GuestReferences.Where(r => r.Id == referenceId).Select(r => r.ImageKey).FirstOrDefaultAsync(ct);
        if (key is null || !await storage.ExistsAsync(key, ct)) throw new NotFoundException("Selfie");
        return await storage.OpenReadAsync(key, ct);
    }

    /// <summary>Admin "resend notification": queues a push/email now (the guest must have turned notifications on).</summary>
    public async Task NotifyAsync(Guid guestId, CancellationToken ct)
    {
        var g = await db.Guests.Where(x => x.Id == guestId)
                    .Select(x => new { HasChannel = x.PushSubscriptionJson != null || (x.NotifyByEmail && x.Email != null) })
                    .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Guest");
        if (!g.HasChannel) throw new BadRequestException("This guest hasn't turned on notifications.");
        notifier.SendNow(guestId);
    }

    public async Task<MatchSummary> ResearchAsync(Guid guestId, CancellationToken ct)
    {
        if (!await db.Guests.AnyAsync(g => g.Id == guestId, ct)) throw new NotFoundException("Guest");
        return await matcher.SearchForGuestAsync(guestId, ct);
    }

    public async Task DeleteAsync(Guid guestId, CancellationToken ct)
    {
        var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == guestId, ct) ?? throw new NotFoundException("Guest");
        db.Guests.Remove(guest);
        db.AuditLogs.Add(new AuditLog { Actor = "admin", Action = "guest_deleted", EntityId = guestId.ToString() });
        await db.SaveChangesAsync(ct);
        await storage.DeletePrefixAsync(StorageKeys.GuestFolder(guest.EventId, guestId), ct);
    }

    private static IQueryable<GuestAdminDto> Summaries(IQueryable<Guest> guests) =>
        guests.AsNoTracking()
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new GuestAdminDto(
                g.Id, g.Name, g.Phone, g.Email, g.ShareLinkId, g.SelfieKey != null,
                g.Matches.Count(m => m.Status == MatchStatus.Auto || m.Status == MatchStatus.Confirmed),
                g.Matches.Count(m => m.Status == MatchStatus.Probable),
                g.Matches.Count(m => m.Status == MatchStatus.Confirmed),
                g.Matches.Count(m => m.Status == MatchStatus.Rejected),
                g.References.Count,
                g.CreatedAt, g.LastSearchedAt, g.PushSubscriptionJson != null, g.NotifyByEmail, g.LastNotifiedAt));
}
