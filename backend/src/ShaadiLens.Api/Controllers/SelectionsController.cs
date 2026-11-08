using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Api.Infrastructure;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Sharing;

namespace ShaadiLens.Api.Controllers;

/// <summary>Selection link (guest JWT): hearts, comments, submit.</summary>
[ApiController]
[Route("api/v1/public/selections")]
[Authorize(Policy = AuthConstants.GuestPolicy)]
public sealed class GuestSelectionsController(SelectionService selections) : ControllerBase
{
    private Application.Guests.GuestContext Ctx => TokenService.GuestContextFrom(User);

    [HttpGet]
    public Task<SelectionStateDto> Get(CancellationToken ct) => selections.GetAsync(Ctx, ct);

    /// <summary>Heart a photo (409 when the limit is reached or the selection is locked).</summary>
    [HttpPost("{photoId:guid}")]
    public Task<SelectionStateDto> Add(Guid photoId, CancellationToken ct) => selections.AddAsync(Ctx, photoId, ct);

    [HttpDelete("{photoId:guid}")]
    public Task<SelectionStateDto> Remove(Guid photoId, CancellationToken ct) => selections.RemoveAsync(Ctx, photoId, ct);

    [HttpPut("{photoId:guid}/comment")]
    public Task<SelectionStateDto> Comment(Guid photoId, CommentRequest req, CancellationToken ct) =>
        selections.CommentAsync(Ctx, photoId, req.Comment, ct);

    [HttpPost("submit")]
    public Task<SelectionStateDto> Submit(CancellationToken ct) => selections.SubmitAsync(Ctx, ct);
}

/// <summary>Admin: see the couple's picks and export them.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class SelectionsController(SelectionService selections) : ControllerBase
{
    [HttpGet("events/{eventId:guid}/selections")]
    public Task<IReadOnlyList<SelectionLinkDto>> List(Guid eventId, [FromQuery] Guid? linkId, CancellationToken ct) =>
        selections.ListAsync(eventId, linkId, ct);

    /// <summary>?format=names (one line for Lightroom) | csv | zip (originals). Optional ?linkId= for one Selection link.</summary>
    [HttpGet("events/{eventId:guid}/selections/export")]
    public async Task Export(Guid eventId, [FromQuery] string format, [FromQuery] Guid? linkId,
        [FromServices] IStorageService storage, CancellationToken ct)
    {
        switch (format)
        {
            case "names":
                Response.ContentType = "text/plain; charset=utf-8";
                await Response.WriteAsync(await selections.ExportNamesAsync(eventId, linkId, ct), ct);
                break;
            case "csv":
                Response.ContentType = "text/csv; charset=utf-8";
                Response.Headers.ContentDisposition = "attachment; filename=\"selection.csv\"";
                // UTF-8 BOM so Excel shows Hindi/Marathi comments correctly.
                await Response.Body.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
                await Response.WriteAsync(await selections.ExportCsvAsync(eventId, linkId, ct), ct);
                break;
            case "zip":
                var entries = await selections.ZipEntriesAsync(eventId, linkId, ct);
                await ZipStreamer.WriteAsync(HttpContext, storage, entries, $"selection-{DateTime.UtcNow:yyyyMMdd}.zip", ct);
                break;
            default:
                throw new BadRequestException("format must be names, csv or zip");
        }
    }

    [HttpPost("share-links/{linkId:guid}/selection/unlock")]
    public async Task<IActionResult> Unlock(Guid linkId, CancellationToken ct)
    {
        await selections.UnlockAsync(linkId, ct);
        return NoContent();
    }
}
