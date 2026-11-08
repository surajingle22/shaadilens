using System.Text;
using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Sharing;

public sealed record SelectionItemDto(Guid PhotoId, string? Comment);

/// <param name="Limit">Max hearts (null = unlimited).</param>
public sealed record SelectionStateDto(int? Limit, int Count, bool Locked, DateTimeOffset? SubmittedAt, IReadOnlyList<SelectionItemDto> Items);

public sealed record CommentRequest(string? Comment);

public sealed record SelectionAdminItemDto(
    Guid PhotoId, string FileName, string Collection, string ThumbUrl, string WebUrl, string? Comment, string? SelectedBy, DateTimeOffset CreatedAt);

public sealed record SelectionLinkDto(
    Guid LinkId, string? Label, int? Limit, int Count, bool Locked, DateTimeOffset? SubmittedAt, string? SubmittedBy,
    IReadOnlyList<SelectionAdminItemDto> Items);

/// <summary>
/// Selection links: the couple browses the whole gallery and hearts the photos they want in the album
/// ("37 / 40"), adds comments, then submits — which locks the picks for the photographer.
/// The limit is enforced here on the server, not just in the UI.
/// </summary>
public sealed class SelectionService(IAppDbContext db, IMediaUrlSigner urls, TimeProvider time)
{
    // ---------- guest (Selection link) ----------

    public async Task<SelectionStateDto> GetAsync(GuestContext ctx, CancellationToken ct)
    {
        var link = await OpenAsync(ctx, ct);
        return await StateAsync(link, ct);
    }

    public async Task<SelectionStateDto> AddAsync(GuestContext ctx, Guid photoId, CancellationToken ct)
    {
        var link = await OpenForChangesAsync(ctx, ct);
        if (!await db.Selections.AnyAsync(s => s.ShareLinkId == link.Id && s.PhotoId == photoId, ct))
        {
            var visible = await db.Photos.AnyAsync(p => p.Id == photoId && p.EventId == ctx.EventId && !p.IsHidden &&
                                                       !p.Collection!.IsHidden && p.StorageKeyWeb != null, ct);
            if (!visible) throw new NotFoundException("Photo");
            var count = await db.Selections.CountAsync(s => s.ShareLinkId == link.Id, ct);
            if (link.SelectionLimit is { } limit && count >= limit)
                throw new ConflictException($"You’ve picked {limit} of {limit}. Remove one to add another.");
            db.Selections.Add(new Selection
            {
                ShareLinkId = link.Id, PhotoId = photoId, SelectedBy = await NameAsync(ctx, ct), CreatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
        }
        return await StateAsync(link, ct);
    }

    public async Task<SelectionStateDto> RemoveAsync(GuestContext ctx, Guid photoId, CancellationToken ct)
    {
        var link = await OpenForChangesAsync(ctx, ct);
        await db.Selections.Where(s => s.ShareLinkId == link.Id && s.PhotoId == photoId).ExecuteDeleteAsync(ct);
        return await StateAsync(link, ct);
    }

    public async Task<SelectionStateDto> CommentAsync(GuestContext ctx, Guid photoId, string? comment, CancellationToken ct)
    {
        var link = await OpenForChangesAsync(ctx, ct);
        var selection = await db.Selections.FirstOrDefaultAsync(s => s.ShareLinkId == link.Id && s.PhotoId == photoId, ct)
                        ?? throw new BadRequestException("Tap the heart first, then add a comment.");
        var text = comment?.Trim();
        selection.Comment = string.IsNullOrEmpty(text) ? null : text.Length > 500 ? text[..500] : text;
        await db.SaveChangesAsync(ct);
        return await StateAsync(link, ct);
    }

    /// <summary>"Submit selection": locks the picks. Submitting nothing is refused.</summary>
    public async Task<SelectionStateDto> SubmitAsync(GuestContext ctx, CancellationToken ct)
    {
        var link = await OpenForChangesAsync(ctx, ct);
        if (!await db.Selections.AnyAsync(s => s.ShareLinkId == link.Id, ct))
            throw new BadRequestException("Pick at least one photo first.");
        var submission = await db.SelectionSubmissions.FirstOrDefaultAsync(s => s.ShareLinkId == link.Id, ct);
        if (submission is null) db.SelectionSubmissions.Add(submission = new SelectionSubmission { ShareLinkId = link.Id });
        submission.Locked = true;
        submission.SubmittedAt = time.GetUtcNow();
        submission.SubmittedBy = await NameAsync(ctx, ct);
        db.AuditLogs.Add(new AuditLog { Actor = ctx.GuestId is { } g ? $"guest:{g}" : $"link:{link.Id}", Action = "selection_submitted", EntityId = link.Id.ToString() });
        await db.SaveChangesAsync(ct);
        return await StateAsync(link, ct);
    }

    // ---------- admin ----------

    public async Task<IReadOnlyList<SelectionLinkDto>> ListAsync(Guid eventId, Guid? linkId, CancellationToken ct)
    {
        var links = await db.ShareLinks.AsNoTracking()
            .Where(l => l.EventId == eventId && l.Type == ShareLinkType.Selection && (linkId == null || l.Id == linkId))
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var ids = links.Select(l => l.Id).ToList();
        var items = await db.Selections.AsNoTracking()
            .Where(s => ids.Contains(s.ShareLinkId))
            .OrderBy(s => s.Photo!.TakenAt ?? s.Photo.UploadedAt).ThenBy(s => s.PhotoId)
            .Select(s => new { s.ShareLinkId, s.PhotoId, s.Photo!.OriginalFileName, Collection = s.Photo.Collection!.Name, s.Comment, s.SelectedBy, s.CreatedAt })
            .ToListAsync(ct);
        var submissions = await db.SelectionSubmissions.AsNoTracking().Where(s => ids.Contains(s.ShareLinkId))
            .ToDictionaryAsync(s => s.ShareLinkId, ct);

        return links.Select(l =>
        {
            var mine = items.Where(i => i.ShareLinkId == l.Id).Select(i => new SelectionAdminItemDto(
                i.PhotoId, i.OriginalFileName, i.Collection, urls.GetUrl(i.PhotoId, PhotoVariant.Thumb), urls.GetUrl(i.PhotoId, PhotoVariant.Web),
                i.Comment, i.SelectedBy, i.CreatedAt)).ToList();
            var sub = submissions.GetValueOrDefault(l.Id);
            return new SelectionLinkDto(l.Id, l.Label, l.SelectionLimit, mine.Count, sub?.Locked == true, sub?.SubmittedAt, sub?.SubmittedBy, mine);
        }).ToList();
    }

    /// <summary>Let the couple change their picks again.</summary>
    public async Task UnlockAsync(Guid linkId, CancellationToken ct)
    {
        var submission = await db.SelectionSubmissions.FirstOrDefaultAsync(s => s.ShareLinkId == linkId, ct)
                         ?? throw new NotFoundException("Submitted selection");
        submission.Locked = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// File names as one comma-separated line, for Lightroom: Library → Filter (\) → Text → Filename →
    /// Contains → paste. Extensions are dropped so "DSC_0012" matches DSC_0012.NEF as well as .JPG.
    /// </summary>
    public async Task<string> ExportNamesAsync(Guid eventId, Guid? linkId, CancellationToken ct) =>
        string.Join(", ", (await ListAsync(eventId, linkId, ct)).SelectMany(l => l.Items)
            .Select(i => Path.GetFileNameWithoutExtension(i.FileName)).Distinct(StringComparer.OrdinalIgnoreCase));

    public async Task<string> ExportCsvAsync(Guid eventId, Guid? linkId, CancellationToken ct)
    {
        var sb = new StringBuilder("link,file_name,collection,comment,selected_by,selected_at\r\n");
        foreach (var link in await ListAsync(eventId, linkId, ct))
        foreach (var i in link.Items)
            sb.Append(Csv(link.Label ?? "Selection")).Append(',').Append(Csv(i.FileName)).Append(',').Append(Csv(i.Collection)).Append(',')
              .Append(Csv(i.Comment)).Append(',').Append(Csv(i.SelectedBy)).Append(',').Append(i.CreatedAt.ToString("yyyy-MM-dd HH:mm")).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Originals of the picks, for the "download picks as ZIP" button.</summary>
    public async Task<IReadOnlyList<ZipEntrySource>> ZipEntriesAsync(Guid eventId, Guid? linkId, CancellationToken ct)
    {
        var photoIds = (await ListAsync(eventId, linkId, ct)).SelectMany(l => l.Items).Select(i => i.PhotoId).Distinct().ToList();
        return await db.Photos.AsNoTracking().Where(p => photoIds.Contains(p.Id))
            .OrderBy(p => p.TakenAt ?? p.UploadedAt)
            .Select(p => new ZipEntrySource(p.Id, p.StorageKeyOriginal, p.OriginalFileName, p.Collection!.Name))
            .ToListAsync(ct);
    }

    // ---------- helpers ----------

    /// <summary>Quotes a CSV field; a leading = + - @ is prefixed with ' so Excel won't run it as a formula.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private async Task<SelectionStateDto> StateAsync(ShareLink link, CancellationToken ct)
    {
        var items = await db.Selections.AsNoTracking().Where(s => s.ShareLinkId == link.Id)
            .OrderBy(s => s.CreatedAt).Select(s => new SelectionItemDto(s.PhotoId, s.Comment)).ToListAsync(ct);
        var sub = await db.SelectionSubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.ShareLinkId == link.Id, ct);
        return new SelectionStateDto(link.SelectionLimit, items.Count, sub?.Locked == true, sub?.SubmittedAt, items);
    }

    private async Task<ShareLink> OpenAsync(GuestContext ctx, CancellationToken ct)
    {
        if (ctx.LinkType != ShareLinkType.Selection) throw new ForbiddenException("This link can't pick photos.");
        var link = await db.ShareLinks.AsNoTracking().Include(l => l.Event)
            .FirstOrDefaultAsync(l => l.Id == ctx.LinkId && l.EventId == ctx.EventId, ct);
        var now = time.GetUtcNow();
        if (link is null || !link.IsUsable(now) || !link.Event!.IsOpen(now))
            throw new ForbiddenException("This gallery isn't open right now. Please check with the photographer.");
        return link;
    }

    private async Task<ShareLink> OpenForChangesAsync(GuestContext ctx, CancellationToken ct)
    {
        var link = await OpenAsync(ctx, ct);
        if (await db.SelectionSubmissions.AnyAsync(s => s.ShareLinkId == link.Id && s.Locked, ct))
            throw new ConflictException("Your selection was submitted and is locked. Ask the photographer to unlock it for changes.");
        return link;
    }

    private async Task<string?> NameAsync(GuestContext ctx, CancellationToken ct) =>
        ctx.GuestId is { } g ? await db.Guests.Where(x => x.Id == g).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
}
