using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Guests;
using ShaadiLens.Application.Sharing;

namespace ShaadiLens.Api.Controllers;

public sealed record SetCoverRequest(Guid? PhotoId);

/// <summary>Admin: share links, QR codes, posters, guests.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class ShareLinksController(ShareLinkService links, GuestAdminService guestAdmin, Application.Events.EventService events) : ControllerBase
{
    [HttpGet("events/{eventId:guid}/share-links")]
    public Task<IReadOnlyList<ShareLinkDto>> List(Guid eventId, CancellationToken ct) => links.ListAsync(eventId, ct);

    [HttpPost("events/{eventId:guid}/share-links")]
    public Task<ShareLinkDto> Create(Guid eventId, SaveShareLinkRequest req, CancellationToken ct) => links.CreateAsync(eventId, req, ct);

    [HttpPut("share-links/{id:guid}")]
    public Task<ShareLinkDto> Update(Guid id, SaveShareLinkRequest req, CancellationToken ct) => links.UpdateAsync(id, req, ct);

    [HttpDelete("share-links/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await links.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Generate / set / remove the PIN. The PIN is shown only in this response.</summary>
    [HttpPost("share-links/{id:guid}/pin")]
    public Task<PinResponse> Pin(Guid id, PinRequest req, CancellationToken ct) => links.SetPinAsync(id, req, ct);

    [HttpGet("share-links/{id:guid}/qr.png")]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct) => File(await links.QrAsync(id, ct), "image/png", "qr.png");

    /// <summary>Printable poster. ?size=a4 (default) or a5.</summary>
    [HttpGet("share-links/{id:guid}/poster.png")]
    public async Task<IActionResult> Poster(Guid id, [FromQuery] string size = "a4", CancellationToken ct = default) =>
        File(await links.PosterAsync(id, size, ct), "image/png", $"poster-{size}.png");

    // ---------- event cover ----------

    [HttpPut("events/{eventId:guid}/cover")]
    public async Task<IActionResult> SetCover(Guid eventId, SetCoverRequest req, CancellationToken ct)
    {
        await events.SetCoverAsync(eventId, req.PhotoId, ct);
        return NoContent();
    }

    // ---------- guests ----------

    [HttpGet("events/{eventId:guid}/guests")]
    public Task<IReadOnlyList<GuestAdminDto>> Guests(Guid eventId, CancellationToken ct) => guestAdmin.ListAsync(eventId, ct);

    [HttpPost("guests/{guestId:guid}/search")]
    public Task<MatchSummary> Research(Guid guestId, CancellationToken ct) => guestAdmin.ResearchAsync(guestId, ct);

    [HttpDelete("guests/{guestId:guid}")]
    public async Task<IActionResult> DeleteGuest(Guid guestId, CancellationToken ct)
    {
        await guestAdmin.DeleteAsync(guestId, ct);
        return NoContent();
    }
}
