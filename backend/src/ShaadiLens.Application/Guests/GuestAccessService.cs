using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Faces;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Guests;

/// <summary>
/// Everything a guest can do through a share link.
///
/// SECURITY RULE: every method starts from a <see cref="GuestContext"/> (from the signed JWT) and only
/// ever returns photos the guest may see:
///   Guest link → only photos they're matched in (Auto/Confirmed; Probable only in the "Might be you?" list)
///   Full link  → all visible photos of the event
/// "Visible" = event published, link active, photo processed, photo and collection not hidden.
/// Media URLs are signed only after that check (see IMediaUrlSigner).
/// </summary>
public sealed class GuestAccessService(
    IAppDbContext db,
    IStorageService storage,
    IFaceEngine faces,
    IFaceMatcher matcher,
    IImageTools images,
    IMediaUrlSigner urls,
    IPushSender push,
    IEmailSender email,
    MatchFeedbackService feedback,
    TimeProvider time,
    ILogger<GuestAccessService> log)
{
    private static readonly MatchStatus[] Shown = [MatchStatus.Auto, MatchStatus.Confirmed];

    /// <summary>The first selfie + up to two extra ones ("a different angle").</summary>
    public const int MaxSelfies = 3;

    // ---------- before login ----------

    public async Task<PublicLinkDto> GetLinkAsync(string token, CancellationToken ct)
    {
        var (link, ev) = await OpenLinkAsync(token, ct);
        return new PublicLinkDto(
            Branding(ev), link.Type, link.PinHash is not null, link.RequireRegistration, link.RegistrationFields,
            SelfieRequired: link.Type == ShareLinkType.Guest, ev.FaceSearchEnabled);
    }

    /// <summary>Checks a PIN. Failures are audit-logged; the API also rate-limits this endpoint.</summary>
    public async Task<bool> VerifyPinAsync(string token, string pin, string? ip, CancellationToken ct)
    {
        var (link, _) = await OpenLinkAsync(token, ct);
        if (link.PinHash is null) return true;
        var ok = Secrets.VerifyPin(pin, link.PinHash);
        if (!ok)
        {
            db.AuditLogs.Add(new AuditLog { Actor = $"link:{link.Id}", Action = "pin_failed", EntityId = link.Id.ToString(), Ip = ip });
            await db.SaveChangesAsync(ct);
        }
        return ok;
    }

    /// <summary>
    /// Registration (form + selfie). For Guest links the selfie is required: it's validated by face-service,
    /// stored, and immediately searched against all faces of the event.
    /// Returns the context to put into the guest JWT.
    /// </summary>
    public async Task<(GuestContext Context, MatchSummary? Matches)> RegisterAsync(
        string token, RegisterRequest req, Stream? selfie, bool pinVerified, string? ip, CancellationToken ct)
    {
        var (link, ev) = await OpenLinkAsync(token, ct);
        if (link.PinHash is not null && !pinVerified) throw new ForbiddenException("Please enter the PIN first.");

        var needsSelfie = link.Type == ShareLinkType.Guest;
        if (needsSelfie && !ev.FaceSearchEnabled) throw new ForbiddenException("Face search is turned off for this event.");
        if (needsSelfie && selfie is null) throw new BadRequestException("A selfie is required to find your photos.");
        if (selfie is not null && !req.Consent)
            throw new BadRequestException("Please tick the consent box so we can use your selfie to find your photos.");

        ValidateFields(link, req);

        // Anonymous Full-gallery viewer: no guest row needed.
        var anonymous = link.Type == ShareLinkType.Full && !link.RequireRegistration && selfie is null;
        if (anonymous) return (new GuestContext(ev.Id, link.Id, null, link.Type), null);

        var guest = new Guest
        {
            EventId = ev.Id,
            ShareLinkId = link.Id,
            Name = Clean(req.Name, 100),
            Phone = Clean(req.Phone, 30),
            Email = Clean(req.Email, 200),
            ConsentGivenAt = selfie is not null ? time.GetUtcNow() : null,
        };

        var checkedSelfie = selfie is null ? null : await CheckSelfieAsync(ev, selfie, ct);

        db.Guests.Add(guest);
        if (checkedSelfie is { } s) guest.SelfieKey = await SaveSelfieAsync(guest, s, ct);
        db.AuditLogs.Add(new AuditLog { Actor = $"guest:{guest.Id}", Action = "guest_registered", EntityId = link.Id.ToString(), Ip = ip });
        await db.SaveChangesAsync(ct);

        var summary = checkedSelfie is not null ? await matcher.SearchForGuestAsync(guest.Id, ct) : null;
        log.LogInformation("Guest {GuestId} registered via link {LinkId}: {Auto} auto / {Probable} probable matches",
            guest.Id, link.Id, summary?.Auto ?? 0, summary?.Probable ?? 0);
        return (new GuestContext(ev.Id, link.Id, guest.Id, link.Type), summary);
    }

    // ---------- after login (guest JWT) ----------

    public async Task<GuestSessionDto> GetSessionAsync(GuestContext ctx, CancellationToken ct)
    {
        var (link, ev, guest) = await OpenSessionAsync(ctx, ct);
        var counts = guest is null
            ? new Dictionary<MatchStatus, int>()
            : await VisibleMatches(ctx, guest.Id)
                .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var selfies = guest is null ? 0
            : await db.GuestReferences.CountAsync(r => r.GuestId == guest.Id && r.Source == ReferenceSource.Selfie, ct);
        return new GuestSessionDto(
            Branding(ev), link.Type, guest?.Name, guest?.SelfieKey is not null,
            link.AllowSingleDownload, link.AllowBulkDownload,
            counts.GetValueOrDefault(MatchStatus.Auto) + counts.GetValueOrDefault(MatchStatus.Confirmed),
            counts.GetValueOrDefault(MatchStatus.Probable),
            guest?.LastSearchedAt, selfies, MaxSelfies,
            new NotificationStatusDto(
                PushPublicKey: push.PublicKey,
                PushOn: guest?.PushSubscriptionJson is not null,
                EmailAvailable: email.IsConfigured && guest?.Email is not null,
                EmailOn: guest?.NotifyByEmail == true && email.IsConfigured));
    }

    /// <summary>"Your photos": Auto + Confirmed matches.</summary>
    public async Task<PagedResult<GuestPhotoDto>> GetMyPhotosAsync(GuestContext ctx, Guid? collectionId, int page, int pageSize, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        await OpenSessionAsync(ctx, ct);
        var photos = VisibleMatches(ctx, guestId).Where(m => Shown.Contains(m.Status)).Select(m => m.Photo!);
        return await PageAsync(photos, collectionId, page, pageSize, ct);
    }

    /// <summary>"Might be you?": Probable matches, best first. Rejected ones never come back.</summary>
    public async Task<IReadOnlyList<GuestPhotoDto>> GetProbableAsync(GuestContext ctx, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        await OpenSessionAsync(ctx, ct);
        var photos = await VisibleMatches(ctx, guestId)
            .Where(m => m.Status == MatchStatus.Probable)
            .OrderByDescending(m => m.Score).Take(100)
            .Select(m => m.Photo!).ToListAsync(ct);
        return photos.Select(ToDto).ToList();
    }

    /// <summary>Full-gallery links: every visible photo.</summary>
    public async Task<PagedResult<GuestPhotoDto>> GetGalleryAsync(GuestContext ctx, Guid? collectionId, int page, int pageSize, CancellationToken ct)
    {
        if (ctx.LinkType != ShareLinkType.Full) throw new ForbiddenException("This link only shows your own photos.");
        await OpenSessionAsync(ctx, ct);
        return await PageAsync(VisiblePhotos(ctx.EventId), collectionId, page, pageSize, ct);
    }

    public async Task<IReadOnlyList<GuestCollectionDto>> GetCollectionsAsync(GuestContext ctx, CancellationToken ct)
    {
        await OpenSessionAsync(ctx, ct);
        var photos = ctx.LinkType == ShareLinkType.Full
            ? VisiblePhotos(ctx.EventId)
            : VisibleMatches(ctx, RequireGuest(ctx)).Where(m => Shown.Contains(m.Status)).Select(m => m.Photo!);
        var counts = await photos.GroupBy(p => p.CollectionId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var collections = await db.Collections.AsNoTracking()
            .Where(c => c.EventId == ctx.EventId && !c.IsHidden)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        // Only tabs that actually have photos for this viewer.
        return collections
            .Select(c => new GuestCollectionDto(c.Id, c.Name, counts.GetValueOrDefault(c.Id)))
            .Where(c => c.PhotoCount > 0)
            .ToList();
    }

    /// <summary>
    /// "Find more photos of me": search again with ALL the guest's references — selfies plus every face they
    /// confirmed — keeping the best similarity per face. New uploads already arrive by themselves (reverse
    /// matching); this mainly helps after "That's me" or a second selfie.
    /// </summary>
    public async Task<MatchSummary> RefreshAsync(GuestContext ctx, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        await OpenSessionAsync(ctx, ct);
        return await matcher.SearchForGuestAsync(guestId, ct);
    }

    /// <summary>
    /// "Notify me when new photos of me arrive": stores the browser's push subscription and/or the email
    /// opt-in. Calling it with no subscription and Email=false turns everything off.
    /// </summary>
    public async Task<NotificationStatusDto> SetNotificationsAsync(GuestContext ctx, NotificationRequest req, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        await OpenSessionAsync(ctx, ct);
        var guest = await db.Guests.FirstAsync(g => g.Id == guestId, ct);

        string? subscription = null;
        if (req.Subscription is { ValueKind: System.Text.Json.JsonValueKind.Object } s)
        {
            subscription = s.GetRawText();
            if (subscription.Length > 4000 || !IsValidPushSubscription(s))
                throw new BadRequestException("That push subscription doesn't look right.");
        }
        guest.PushSubscriptionJson = subscription;
        guest.NotifyByEmail = req.Email && guest.Email is not null;
        await db.SaveChangesAsync(ct);
        return new NotificationStatusDto(push.PublicKey, guest.PushSubscriptionJson is not null,
            email.IsConfigured && guest.Email is not null, guest.NotifyByEmail && email.IsConfigured);
    }

    /// <summary>{ endpoint: "https://…", keys: { p256dh, auth } } — what PushManager.subscribe() returns.</summary>
    private static bool IsValidPushSubscription(System.Text.Json.JsonElement s) =>
        s.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == System.Text.Json.JsonValueKind.String &&
        Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        s.TryGetProperty("keys", out var keys) && keys.ValueKind == System.Text.Json.JsonValueKind.Object &&
        keys.TryGetProperty("p256dh", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String &&
        keys.TryGetProperty("auth", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String;

    /// <summary>"Yes, that's me" (usually on a "Might be you?" photo).</summary>
    public Task ConfirmAsync(GuestContext ctx, Guid photoId, CancellationToken ct) =>
        DecideAsync(ctx, photoId, MatchStatus.Confirmed, ct);

    /// <summary>"Not me": the photo disappears for this guest and never comes back.</summary>
    public Task RejectAsync(GuestContext ctx, Guid photoId, CancellationToken ct) =>
        DecideAsync(ctx, photoId, MatchStatus.Rejected, ct);

    private async Task DecideAsync(GuestContext ctx, Guid photoId, MatchStatus decision, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        await OpenSessionAsync(ctx, ct);
        // Only photos this guest was matched with (and may currently see) can be decided on.
        var match = await db.GuestMatches.FirstOrDefaultAsync(m =>
                        m.GuestId == guestId && m.PhotoId == photoId && m.Photo!.EventId == ctx.EventId &&
                        !m.Photo.IsHidden && !m.Photo.Collection!.IsHidden, ct)
                    ?? throw new NotFoundException("Photo");
        await feedback.SetDecisionAsync(match, decision, $"guest:{guestId}", ct);
    }

    /// <summary>
    /// Extra selfie (a different angle, with/without glasses…): one more reference embedding, then search again.
    /// </summary>
    public async Task<MatchSummary> AddSelfieAsync(GuestContext ctx, Stream selfie, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        var (_, ev, _) = await OpenSessionAsync(ctx, ct);
        var guest = await db.Guests.FirstAsync(g => g.Id == guestId, ct);
        if (guest.ConsentGivenAt is null) throw new ForbiddenException("Take your first selfie from the gallery link.");
        if (!ev.FaceSearchEnabled) throw new ForbiddenException("Face search is turned off for this event.");
        var selfies = await db.GuestReferences.CountAsync(r => r.GuestId == guestId && r.Source == ReferenceSource.Selfie, ct);
        if (selfies >= MaxSelfies) throw new BadRequestException($"You already added {MaxSelfies} selfies. Try “Find more” instead.");

        var s = await CheckSelfieAsync(ev, selfie, ct);
        await SaveSelfieAsync(guest, s, ct);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Guest {GuestId} added selfie #{Number}", guestId, selfies + 1);
        return await matcher.SearchForGuestAsync(guestId, ct);
    }

    /// <summary>Signed URL of the original, if this guest may see the photo and downloads are allowed.</summary>
    public async Task<string> GetDownloadUrlAsync(GuestContext ctx, Guid photoId, string? ip, CancellationToken ct)
    {
        var (link, _, _) = await OpenSessionAsync(ctx, ct);
        if (!link.AllowSingleDownload) throw new ForbiddenException("Downloads are turned off for this gallery.");
        if (!await CanSeePhotoAsync(ctx, photoId, ct)) throw new NotFoundException("Photo");
        db.AuditLogs.Add(new AuditLog { Actor = Actor(ctx), Action = "download", EntityId = photoId.ToString(), Ip = ip });
        await db.SaveChangesAsync(ct);
        return urls.GetUrl(photoId, PhotoVariant.Original);
    }

    public async Task EnsureBulkDownloadAllowedAsync(GuestContext ctx, CancellationToken ct)
    {
        var (link, _, _) = await OpenSessionAsync(ctx, ct);
        if (!link.AllowBulkDownload) throw new ForbiddenException("Download all is turned off for this gallery.");
        if (ctx.LinkType != ShareLinkType.Full) RequireGuest(ctx);
    }

    /// <summary>Files for "Download all" (originals), after the same permission checks.</summary>
    public async Task<IReadOnlyList<ZipEntrySource>> GetZipEntriesAsync(GuestContext ctx, Guid? collectionId, string? ip, CancellationToken ct)
    {
        var (link, _, _) = await OpenSessionAsync(ctx, ct);
        if (!link.AllowBulkDownload) throw new ForbiddenException("Download all is turned off for this gallery.");

        var photos = ctx.LinkType == ShareLinkType.Full
            ? VisiblePhotos(ctx.EventId)
            : VisibleMatches(ctx, RequireGuest(ctx)).Where(m => Shown.Contains(m.Status)).Select(m => m.Photo!);
        if (collectionId is { } cid) photos = photos.Where(p => p.CollectionId == cid);

        var entries = await photos
            .OrderBy(p => p.TakenAt ?? p.UploadedAt)
            .Select(p => new ZipEntrySource(p.Id, p.StorageKeyOriginal, p.OriginalFileName, p.Collection!.Name))
            .ToListAsync(ct);
        db.AuditLogs.Add(new AuditLog { Actor = Actor(ctx), Action = "download_all", Details = $"{entries.Count} photos", Ip = ip });
        await db.SaveChangesAsync(ct);
        return entries;
    }

    /// <summary>"Delete my data": guest row, selfie file, reference embeddings and matches.</summary>
    public async Task DeleteMeAsync(GuestContext ctx, string? ip, CancellationToken ct)
    {
        var guestId = RequireGuest(ctx);
        var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == guestId && g.EventId == ctx.EventId, ct);
        if (guest is null) return;
        db.Guests.Remove(guest); // references + matches cascade
        db.AuditLogs.Add(new AuditLog { Actor = $"guest:{guestId}", Action = "guest_deleted_self", Ip = ip });
        await db.SaveChangesAsync(ct);
        await storage.DeletePrefixAsync(StorageKeys.GuestFolder(ctx.EventId, guestId), ct);
    }

    /// <summary>The one permission check for a single photo.</summary>
    public async Task<bool> CanSeePhotoAsync(GuestContext ctx, Guid photoId, CancellationToken ct) =>
        ctx.LinkType == ShareLinkType.Full
            ? await VisiblePhotos(ctx.EventId).AnyAsync(p => p.Id == photoId, ct)
            : ctx.GuestId is { } gid && await VisibleMatches(ctx, gid)
                .AnyAsync(m => m.PhotoId == photoId && m.Status != MatchStatus.Rejected, ct);

    // ---------- helpers ----------

    private sealed record CheckedSelfie(byte[] Jpeg, Vector Embedding);

    /// <summary>Normalise (upright JPEG, no metadata) and let face-service check it. Throws SelfieRejectedException.</summary>
    private async Task<CheckedSelfie> CheckSelfieAsync(Event ev, Stream selfie, CancellationToken ct)
    {
        var jpeg = await images.NormalizeSelfieAsync(selfie, ct);
        var check = await faces.CheckSelfieAsync(new MemoryStream(jpeg), ev.FaceSettings.MaxSelfieYaw, ct);
        if (!check.Ok || check.Embedding is null)
            throw new SelfieRejectedException(check.Code, check.Reason ?? "Please try another selfie.");
        return new CheckedSelfie(jpeg, new Vector(check.Embedding));
    }

    /// <summary>Stores the selfie file and adds its embedding as a reference (caller saves changes).</summary>
    private async Task<string> SaveSelfieAsync(Guest guest, CheckedSelfie s, CancellationToken ct)
    {
        var key = StorageKeys.Selfie(guest.EventId, guest.Id, Guid.CreateVersion7());
        await storage.SaveAsync(key, new MemoryStream(s.Jpeg), ct);
        db.GuestReferences.Add(new GuestReference
        {
            GuestId = guest.Id, Embedding = s.Embedding, Source = ReferenceSource.Selfie, ImageKey = key,
            CreatedAt = time.GetUtcNow(),
        });
        return key;
    }

    private IQueryable<Photo> VisiblePhotos(Guid eventId) =>
        db.Photos.AsNoTracking().Where(p =>
            p.EventId == eventId && !p.IsHidden && !p.Collection!.IsHidden &&
            p.StorageKeyWeb != null && p.Status != PhotoStatus.Failed);

    private IQueryable<GuestMatch> VisibleMatches(GuestContext ctx, Guid guestId) =>
        db.GuestMatches.AsNoTracking().Where(m =>
            m.GuestId == guestId && m.Photo!.EventId == ctx.EventId &&
            !m.Photo.IsHidden && !m.Photo.Collection!.IsHidden && m.Photo.StorageKeyWeb != null);

    private async Task<PagedResult<GuestPhotoDto>> PageAsync(IQueryable<Photo> photos, Guid? collectionId, int page, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        if (collectionId is { } cid) photos = photos.Where(p => p.CollectionId == cid);
        var total = await photos.CountAsync(ct);
        var items = await photos.OrderBy(p => p.TakenAt ?? p.UploadedAt).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<GuestPhotoDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    private GuestPhotoDto ToDto(Photo p) => new(
        p.Id, p.CollectionId, p.Width, p.Height, p.TakenAt,
        urls.GetUrl(p.Id, PhotoVariant.Thumb), urls.GetUrl(p.Id, PhotoVariant.Web));

    private EventBrandingDto Branding(Event ev) => new(
        ev.Name, ev.Date, ev.Venue, ev.ThemeColor, ev.TitleFont,
        ev.CoverPhotoId is { } cover ? urls.GetUrl(cover, PhotoVariant.Web) : null);

    /// <summary>Link must exist, be active/unexpired, and its event published.</summary>
    private async Task<(ShareLink Link, Event Event)> OpenLinkAsync(string token, CancellationToken ct)
    {
        var link = await db.ShareLinks.AsNoTracking().Include(l => l.Event)
                       .FirstOrDefaultAsync(l => l.Token == token, ct)
                   ?? throw new NotFoundException("Gallery link");
        if (!link.IsUsable(time.GetUtcNow()) || !link.Event!.IsPublished)
            throw new ForbiddenException("This gallery isn't open right now. Please check with the couple.");
        return (link, link.Event);
    }

    /// <summary>Re-checks on every request: the admin may have closed the link or the guest deleted their data.</summary>
    private async Task<(ShareLink Link, Event Event, Guest? Guest)> OpenSessionAsync(GuestContext ctx, CancellationToken ct)
    {
        var link = await db.ShareLinks.AsNoTracking().Include(l => l.Event)
            .FirstOrDefaultAsync(l => l.Id == ctx.LinkId && l.EventId == ctx.EventId, ct);
        if (link is null || !link.IsUsable(time.GetUtcNow()) || !link.Event!.IsPublished)
            throw new ForbiddenException("This gallery isn't open right now. Please check with the couple.");

        Guest? guest = null;
        if (ctx.GuestId is { } gid)
        {
            guest = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gid && g.EventId == ctx.EventId, ct)
                    ?? throw new ForbiddenException("Your session has ended. Please open the link again.");
        }
        return (link, link.Event, guest);
    }

    private static Guid RequireGuest(GuestContext ctx) =>
        ctx.GuestId ?? throw new ForbiddenException("Take a selfie first to see your photos.");

    private static string Actor(GuestContext ctx) => ctx.GuestId is { } g ? $"guest:{g}" : $"link:{ctx.LinkId}";

    private static void ValidateFields(ShareLink link, RegisterRequest req)
    {
        if (!link.RequireRegistration) return;
        var f = link.RegistrationFields;
        if (f.HasFlag(RegistrationFields.Name) && string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Please enter your name.");
        if (f.HasFlag(RegistrationFields.Phone) && (req.Phone is null || req.Phone.Count(char.IsDigit) is < 10 or > 15))
            throw new BadRequestException("Please enter a valid phone number.");
        if (f.HasFlag(RegistrationFields.Email) && !MailAddress.TryCreate(req.Email ?? "", out _))
            throw new BadRequestException("Please enter a valid email address.");
    }

    private static string? Clean(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
