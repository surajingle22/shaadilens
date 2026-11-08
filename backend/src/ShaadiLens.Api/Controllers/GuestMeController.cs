using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Api.Controllers;

public sealed record DownloadAllRequest(Guid? CollectionId);
public sealed record UrlResponse(string Url);

/// <summary>Everything a logged-in guest (guest JWT as Bearer token) can do.</summary>
[ApiController]
[Route("api/v1/public")]
[Authorize(Policy = AuthConstants.GuestPolicy)]
public sealed class GuestMeController(GuestAccessService guests, TokenService tokens) : ControllerBase
{
    private const string ZipTicketPurpose = "zip";

    private GuestContext Ctx => TokenService.GuestContextFrom(User);

    [HttpGet("me")]
    public Task<GuestSessionDto> Me(CancellationToken ct) => guests.GetSessionAsync(Ctx, ct);

    /// <summary>"Your photos" (Guest links).</summary>
    [HttpGet("me/photos")]
    public Task<PagedResult<GuestPhotoDto>> MyPhotos([FromQuery] Guid? collectionId, [FromQuery] int page = 1, [FromQuery] int pageSize = 60, CancellationToken ct = default) =>
        guests.GetMyPhotosAsync(Ctx, collectionId, page, pageSize, ct);

    /// <summary>"Might be you?" strip.</summary>
    [HttpGet("me/probable")]
    public Task<IReadOnlyList<GuestPhotoDto>> Probable(CancellationToken ct) => guests.GetProbableAsync(Ctx, ct);

    /// <summary>"Find more photos of me" — search all faces again with all my references.</summary>
    [HttpPost("me/find-more")]
    [HttpPost("me/refresh")]
    [EnableRateLimiting(AuthSetup.RefreshRateLimit)]
    public Task<MatchSummary> Refresh(CancellationToken ct) => guests.RefreshAsync(Ctx, ct);

    /// <summary>"Yes, that's me".</summary>
    [HttpPost("me/matches/{photoId:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid photoId, CancellationToken ct)
    {
        await guests.ConfirmAsync(Ctx, photoId, ct);
        return NoContent();
    }

    /// <summary>"Not me".</summary>
    [HttpPost("me/matches/{photoId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid photoId, CancellationToken ct)
    {
        await guests.RejectAsync(Ctx, photoId, ct);
        return NoContent();
    }

    /// <summary>Extra selfie (multipart field "selfie"), then search again.</summary>
    [HttpPost("me/selfie")]
    [EnableRateLimiting(AuthSetup.RegisterRateLimit)]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<MatchSummary> AddSelfie(IFormFile? selfie, CancellationToken ct)
    {
        if (selfie is null || selfie.Length == 0) throw new BadRequestException("Please take a selfie first.");
        await using var stream = selfie.OpenReadStream();
        return await guests.AddSelfieAsync(Ctx, stream, ct);
    }

    /// <summary>Whole gallery (Full links only).</summary>
    [HttpGet("gallery")]
    public Task<PagedResult<GuestPhotoDto>> Gallery([FromQuery] Guid? collectionId, [FromQuery] int page = 1, [FromQuery] int pageSize = 60, CancellationToken ct = default) =>
        guests.GetGalleryAsync(Ctx, collectionId, page, pageSize, ct);

    /// <summary>Collection tabs with photo counts for this viewer.</summary>
    [HttpGet("collections")]
    public Task<IReadOnlyList<GuestCollectionDto>> Collections(CancellationToken ct) => guests.GetCollectionsAsync(Ctx, ct);

    /// <summary>Returns a short-lived signed URL of the original photo; the browser then navigates to it.</summary>
    [HttpPost("photos/{photoId:guid}/download")]
    public async Task<UrlResponse> Download(Guid photoId, CancellationToken ct) =>
        new(await guests.GetDownloadUrlAsync(Ctx, photoId, AuthSetup.Ip(HttpContext), ct));

    /// <summary>
    /// "Download all": checks permission now and returns a 10-minute ticket URL for the ZIP. (A plain link
    /// can't carry the Bearer header, so the ticket is the permission.)
    /// </summary>
    [HttpPost("me/download-all")]
    public async Task<UrlResponse> DownloadAll(DownloadAllRequest req, CancellationToken ct)
    {
        var ctx = Ctx;
        // Fail fast (e.g. bulk download disabled) before handing out a ticket.
        await guests.EnsureBulkDownloadAllowedAsync(ctx, ct);
        var values = new Dictionary<string, string>
        {
            [TokenService.EventIdClaim] = ctx.EventId.ToString(),
            [TokenService.LinkIdClaim] = ctx.LinkId.ToString(),
            [TokenService.LinkTypeClaim] = ctx.LinkType.ToString(),
        };
        if (ctx.GuestId is { } gid) values[TokenService.GuestIdClaim] = gid.ToString();
        if (req.CollectionId is { } cid) values["cid"] = cid.ToString();
        var ticket = tokens.CreateTicket(ZipTicketPurpose, TimeSpan.FromMinutes(10), values);
        return new UrlResponse($"/api/v1/public/zip?ticket={Uri.EscapeDataString(ticket)}");
    }

    /// <summary>Streams the ZIP: photos are written one by one, nothing is built in memory or on disk first.</summary>
    [HttpGet("zip")]
    [AllowAnonymous]
    public async Task Zip([FromQuery] string? ticket, [FromServices] IStorageService storage, CancellationToken ct)
    {
        var t = await tokens.ReadTicketAsync(ZipTicketPurpose, ticket);
        if (t is null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            await Response.WriteAsync("This download link has expired. Go back and tap Download all again.", ct);
            return;
        }
        var ctx = new GuestContext(
            Guid.Parse(t[TokenService.EventIdClaim]), Guid.Parse(t[TokenService.LinkIdClaim]),
            t.TryGetValue(TokenService.GuestIdClaim, out var g) ? Guid.Parse(g) : null,
            Enum.Parse<ShareLinkType>(t[TokenService.LinkTypeClaim]));
        Guid? collectionId = t.TryGetValue("cid", out var c) ? Guid.Parse(c) : null;

        var entries = await guests.GetZipEntriesAsync(ctx, collectionId, AuthSetup.Ip(HttpContext), ct);

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"wedding-photos-{DateTime.UtcNow:yyyyMMdd}.zip\"";
        // ZipArchive writes synchronously when it finishes an entry; allow that for this response only.
        HttpContext.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

        using var zip = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = UniqueName(used, $"{Safe(entry.CollectionName)}/{Path.ChangeExtension(Safe(entry.FileName), Path.GetExtension(entry.StorageKey))}");
            // JPEGs are already compressed — storing them as-is is fastest.
            var zipEntry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            await using var target = zipEntry.Open();
            await using var source = await storage.OpenReadAsync(entry.StorageKey, ct);
            await source.CopyToAsync(target, ct);
        }
    }

    [HttpDelete("me")]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        await guests.DeleteMeAsync(Ctx, AuthSetup.Ip(HttpContext), ct);
        return NoContent();
    }

    private static string Safe(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars().Append('/').Append('\\').ToArray())).Trim();

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++)
            candidate = $"{Path.ChangeExtension(name, null)} ({i}){Path.GetExtension(name)}";
        return candidate;
    }
}
