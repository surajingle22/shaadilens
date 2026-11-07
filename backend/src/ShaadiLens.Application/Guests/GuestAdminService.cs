using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Guests;

public sealed record GuestAdminDto(
    Guid Id, string? Name, string? Phone, string? Email, Guid ShareLinkId, bool HasSelfie,
    int MatchCount, int ProbableCount, DateTimeOffset CreatedAt, DateTimeOffset? LastSearchedAt);

/// <summary>Admin view of registered guests.</summary>
public sealed class GuestAdminService(IAppDbContext db, IStorageService storage, IFaceMatcher matcher)
{
    public async Task<IReadOnlyList<GuestAdminDto>> ListAsync(Guid eventId, CancellationToken ct) =>
        await db.Guests.AsNoTracking().Where(g => g.EventId == eventId)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new GuestAdminDto(
                g.Id, g.Name, g.Phone, g.Email, g.ShareLinkId, g.SelfieKey != null,
                g.Matches.Count(m => m.Status == MatchStatus.Auto || m.Status == MatchStatus.Confirmed),
                g.Matches.Count(m => m.Status == MatchStatus.Probable),
                g.CreatedAt, g.LastSearchedAt))
            .ToListAsync(ct);

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
}
