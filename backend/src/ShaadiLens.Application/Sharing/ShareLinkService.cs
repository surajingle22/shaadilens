using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Sharing;

public sealed record ShareLinkDto(
    Guid Id, ShareLinkType Type, string? Label, string Token, string Url, bool HasPin,
    bool RequireRegistration, RegistrationFields RegistrationFields, bool AllowSingleDownload,
    bool AllowBulkDownload, DateTimeOffset? ExpiresAt, bool IsActive, int GuestCount, DateTimeOffset CreatedAt,
    int? SelectionLimit);

public sealed record SaveShareLinkRequest(
    ShareLinkType Type, string? Label, bool RequireRegistration, RegistrationFields RegistrationFields,
    bool AllowSingleDownload, bool AllowBulkDownload, DateTimeOffset? ExpiresAt, bool IsActive,
    int? SelectionLimit = null);

/// <param name="Action">"generate" (random 6 digits), "set" (use Pin), "remove".</param>
public sealed record PinRequest(string Action, string? Pin);

/// <summary>The PIN is only ever returned here, right after setting it — we store just a hash.</summary>
public sealed record PinResponse(bool HasPin, string? Pin);

public sealed class SaveShareLinkRequestValidator : AbstractValidator<SaveShareLinkRequest>
{
    public SaveShareLinkRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.SelectionLimit).InclusiveBetween(1, 5000).When(x => x.SelectionLimit is not null);
        RuleFor(x => x.Label).MaximumLength(100);
    }
}

public sealed class PinRequestValidator : AbstractValidator<PinRequest>
{
    public PinRequestValidator()
    {
        RuleFor(x => x.Action).Must(a => a is "generate" or "set" or "remove");
        RuleFor(x => x.Pin).NotEmpty().Matches("^[0-9]{6}$").When(x => x.Action == "set")
            .WithMessage("PIN must be exactly 6 digits");
    }
}

/// <summary>Admin use cases: share links, PINs, QR codes and posters.</summary>
public sealed class ShareLinkService(IAppDbContext db, IShareArtwork artwork, IOptions<AppOptions> app)
{
    public async Task<IReadOnlyList<ShareLinkDto>> ListAsync(Guid eventId, CancellationToken ct)
    {
        var links = await db.ShareLinks.AsNoTracking().Where(l => l.EventId == eventId)
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var counts = await db.Guests.Where(g => g.EventId == eventId)
            .GroupBy(g => g.ShareLinkId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return links.Select(l => ToDto(l, counts.GetValueOrDefault(l.Id))).ToList();
    }

    public async Task<ShareLinkDto> CreateAsync(Guid eventId, SaveShareLinkRequest req, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == eventId, ct)) throw new NotFoundException("Event");
        var link = new ShareLink { EventId = eventId, Token = Secrets.NewLinkToken() };
        Apply(link, req);
        db.ShareLinks.Add(link);
        await db.SaveChangesAsync(ct);
        return ToDto(link, 0);
    }

    public async Task<ShareLinkDto> UpdateAsync(Guid id, SaveShareLinkRequest req, CancellationToken ct)
    {
        var link = await FindAsync(id, ct);
        if (req.Type != link.Type) throw new BadRequestException("A link's type can't be changed — create a new link instead");
        Apply(link, req);
        await db.SaveChangesAsync(ct);
        return ToDto(link, await db.Guests.CountAsync(g => g.ShareLinkId == id, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var link = await FindAsync(id, ct);
        db.ShareLinks.Remove(link); // guests registered through it (and their selfie data) cascade
        await db.SaveChangesAsync(ct);
    }

    public async Task<PinResponse> SetPinAsync(Guid id, PinRequest req, CancellationToken ct)
    {
        var link = await FindAsync(id, ct);
        string? pin = req.Action switch
        {
            "generate" => Secrets.NewPin(),
            "set" => req.Pin,
            _ => null,
        };
        link.PinHash = pin is null ? null : Secrets.HashPin(pin);
        await db.SaveChangesAsync(ct);
        return new PinResponse(pin is not null, pin);
    }

    public async Task<byte[]> QrAsync(Guid id, CancellationToken ct) => artwork.QrPng(UrlFor(await FindAsync(id, ct)));

    public async Task<byte[]> PosterAsync(Guid id, string size, CancellationToken ct)
    {
        var link = await FindAsync(id, ct);
        var ev = await db.Events.AsNoTracking().FirstAsync(e => e.Id == link.EventId, ct);
        var subtitle = string.Join("  ·  ", new[]
        {
            ev.Date?.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-IN")),
            ev.Venue,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return artwork.PosterPng(
            new PosterInfo(ev.Name, subtitle.Length > 0 ? subtitle : null, ev.ThemeColor, UrlFor(link), link.Type.ShowsWholeGallery()),
            size is "a5" ? "a5" : "a4");
    }

    private string UrlFor(ShareLink link) => $"{app.Value.PublicBaseUrl.TrimEnd('/')}/g/{link.Token}";

    private async Task<ShareLink> FindAsync(Guid id, CancellationToken ct) =>
        await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Share link");

    private static void Apply(ShareLink link, SaveShareLinkRequest req)
    {
        link.Type = req.Type;
        link.Label = req.Label?.Trim();
        link.RequireRegistration = req.RequireRegistration;
        link.RegistrationFields = req.RegistrationFields;
        link.AllowSingleDownload = req.AllowSingleDownload;
        link.AllowBulkDownload = req.AllowBulkDownload;
        link.ExpiresAt = req.ExpiresAt;
        link.IsActive = req.IsActive;
        link.SelectionLimit = req.Type == ShareLinkType.Selection ? req.SelectionLimit : null;
        if (req.Type == ShareLinkType.VipGuest)
        {
            // VIP = selfie only: no form (the consent checkbox is still shown — it's required for face data).
            link.RequireRegistration = false;
            link.RegistrationFields = RegistrationFields.None;
        }
    }

    private ShareLinkDto ToDto(ShareLink l, int guests) => new(
        l.Id, l.Type, l.Label, l.Token, UrlFor(l), l.PinHash is not null, l.RequireRegistration, l.RegistrationFields,
        l.AllowSingleDownload, l.AllowBulkDownload, l.ExpiresAt, l.IsActive, guests, l.CreatedAt, l.SelectionLimit);
}
