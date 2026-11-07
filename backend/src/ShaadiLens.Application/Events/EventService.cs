using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Events;

/// <summary>Admin use cases for events and their collections.</summary>
public sealed class EventService(IAppDbContext db, IStorageService storage, IMediaUrlSigner urls)
{
    public static readonly string[] DefaultCollections = ["Haldi", "Mehendi", "Sangeet", "Wedding", "Reception"];

    public async Task<IReadOnlyList<EventSummaryDto>> ListAsync(CancellationToken ct)
    {
        return await db.Events
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new EventSummaryDto(
                e.Id, e.Name, e.Slug, e.Date, e.Venue, e.IsPublished,
                db.Photos.Count(p => p.EventId == e.Id),
                db.Guests.Count(g => g.EventId == e.Id),
                e.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<EventDto> GetAsync(Guid id, CancellationToken ct)
    {
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct)
                 ?? throw new NotFoundException("Event");
        return await ToDtoAsync(ev, ct);
    }

    /// <summary>Creates an event with the usual five wedding functions as collections.</summary>
    public async Task<EventDto> CreateAsync(SaveEventRequest req, CancellationToken ct)
    {
        var ev = new Event();
        Apply(ev, req);
        ev.Slug = await UniqueSlugAsync(Slug.From(req.Name), ct);
        ev.Collections = DefaultCollections.Select((name, i) => new Collection { Name = name, SortOrder = i }).ToList();
        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(ev, ct);
    }

    public async Task<EventDto> UpdateAsync(Guid id, SaveEventRequest req, CancellationToken ct)
    {
        var ev = await FindAsync(id, ct);
        Apply(ev, req);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(ev, ct);
    }

    public async Task SetPublishedAsync(Guid id, bool published, CancellationToken ct)
    {
        var ev = await FindAsync(id, ct);
        ev.IsPublished = published;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Use one of the event's photos as the cover (null = no cover).</summary>
    public async Task SetCoverAsync(Guid id, Guid? photoId, CancellationToken ct)
    {
        var ev = await FindAsync(id, ct);
        if (photoId is { } pid && !await db.Photos.AnyAsync(p => p.Id == pid && p.EventId == id && p.StorageKeyWeb != null, ct))
            throw new BadRequestException("Cover must be a processed photo of this event");
        ev.CoverPhotoId = photoId;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateFaceSettingsAsync(Guid id, FaceSettingsDto s, CancellationToken ct)
    {
        if (s.ProbableThreshold > s.MatchThreshold)
            throw new BadRequestException("ProbableThreshold must be ≤ MatchThreshold");
        var ev = await FindAsync(id, ct);
        ev.FaceSettings = new FaceSettings
        {
            MatchThreshold = s.MatchThreshold,
            ProbableThreshold = s.ProbableThreshold,
            MinFaceSizePx = s.MinFaceSizePx,
            MinDetScore = s.MinDetScore,
            MaxSelfieYaw = s.MaxSelfieYaw,
        };
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the event, all rows (cascade) and all files on disk.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var ev = await FindAsync(id, ct);
        db.Events.Remove(ev);
        await db.SaveChangesAsync(ct);
        await storage.DeletePrefixAsync($"events/{id}", ct);
    }

    public async Task<EventStatsDto> GetStatsAsync(Guid id, CancellationToken ct)
    {
        _ = await FindAsync(id, ct);
        var byStatus = await db.Photos.Where(p => p.EventId == id)
            .GroupBy(p => p.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        int Count(PhotoStatus s) => byStatus.GetValueOrDefault(s);

        var total = byStatus.Values.Sum();
        var processed = Count(PhotoStatus.FacesDetected) + Count(PhotoStatus.Matched);
        return new EventStatsDto(
            Photos: total,
            Processed: processed,
            Pending: Count(PhotoStatus.Uploaded) + Count(PhotoStatus.Resized),
            Failed: Count(PhotoStatus.Failed),
            Faces: await db.Faces.CountAsync(f => f.EventId == id, ct),
            Guests: await db.Guests.CountAsync(g => g.EventId == id, ct),
            Matches: await db.GuestMatches.CountAsync(m => m.Photo!.EventId == id, ct),
            ByStatus: byStatus);
    }

    // ---------- collections ----------

    public async Task<CollectionDto> AddCollectionAsync(Guid eventId, SaveCollectionRequest req, CancellationToken ct)
    {
        _ = await FindAsync(eventId, ct);
        var max = await db.Collections.Where(c => c.EventId == eventId).MaxAsync(c => (int?)c.SortOrder, ct) ?? -1;
        var col = new Collection { EventId = eventId, Name = req.Name.Trim(), IsHidden = req.IsHidden, SortOrder = max + 1 };
        db.Collections.Add(col);
        await db.SaveChangesAsync(ct);
        return new CollectionDto(col.Id, col.Name, col.SortOrder, col.IsHidden, 0);
    }

    public async Task UpdateCollectionAsync(Guid eventId, Guid id, SaveCollectionRequest req, CancellationToken ct)
    {
        var col = await db.Collections.FirstOrDefaultAsync(c => c.Id == id && c.EventId == eventId, ct)
                  ?? throw new NotFoundException("Collection");
        col.Name = req.Name.Trim();
        col.IsHidden = req.IsHidden;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Only empty collections can be deleted (move or delete the photos first).</summary>
    public async Task DeleteCollectionAsync(Guid eventId, Guid id, CancellationToken ct)
    {
        var col = await db.Collections.FirstOrDefaultAsync(c => c.Id == id && c.EventId == eventId, ct)
                  ?? throw new NotFoundException("Collection");
        if (await db.Photos.AnyAsync(p => p.CollectionId == id, ct))
            throw new ConflictException("Collection still has photos. Move or delete them first.");
        db.Collections.Remove(col);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets SortOrder from the order of ids given (drag-and-drop reorder).</summary>
    public async Task ReorderCollectionsAsync(Guid eventId, IReadOnlyList<Guid> orderedIds, CancellationToken ct)
    {
        var cols = await db.Collections.Where(c => c.EventId == eventId).ToListAsync(ct);
        foreach (var col in cols)
        {
            var idx = orderedIds.ToList().IndexOf(col.Id);
            col.SortOrder = idx >= 0 ? idx : orderedIds.Count + col.SortOrder;
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------- helpers ----------

    private async Task<Event> FindAsync(Guid id, CancellationToken ct) =>
        await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Event");

    private static void Apply(Event ev, SaveEventRequest req)
    {
        ev.Name = req.Name.Trim();
        ev.Date = req.Date;
        ev.Venue = req.Venue?.Trim();
        ev.ThemeColor = req.ThemeColor ?? ev.ThemeColor;
        ev.TitleFont = req.TitleFont;
        ev.WatermarkEnabled = req.WatermarkEnabled;
        ev.WatermarkText = req.WatermarkText;
        ev.FaceSearchEnabled = req.FaceSearchEnabled;
        ev.ExpiresAt = req.ExpiresAt;
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var i = 2; await db.Events.AnyAsync(e => e.Slug == slug, ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    private async Task<EventDto> ToDtoAsync(Event ev, CancellationToken ct)
    {
        var collections = await db.Collections.Where(c => c.EventId == ev.Id)
            .OrderBy(c => c.SortOrder)
            .Select(c => new CollectionDto(c.Id, c.Name, c.SortOrder, c.IsHidden, db.Photos.Count(p => p.CollectionId == c.Id)))
            .ToListAsync(ct);
        return new EventDto(ev.Id, ev.Name, ev.Slug, ev.Date, ev.Venue, ev.ThemeColor, ev.TitleFont,
            ev.WatermarkEnabled, ev.WatermarkText, ev.IsPublished, ev.FaceSearchEnabled, ev.CoverPhotoId,
            ev.CoverPhotoId is { } cover ? urls.GetUrl(cover, PhotoVariant.Web) : null, ev.ExpiresAt, ev.CreatedAt,
            FaceSettingsDto.From(ev.FaceSettings), collections);
    }
}
