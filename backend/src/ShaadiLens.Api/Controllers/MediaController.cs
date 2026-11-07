using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Storage;

namespace ShaadiLens.Api.Controllers;

/// <summary>
/// Serves photo files. No login needed: the signed URL itself is the permission
/// (it was only handed out to someone allowed to see the photo — see MediaUrlSigner).
/// </summary>
[ApiController]
[Route("media")]
public sealed class MediaController(IAppDbContext db, IMediaUrlSigner signer, LocalDiskStorage storage, TimeProvider time) : ControllerBase
{
    [HttpGet("{photoId:guid}/{variant}")]
    public async Task<IActionResult> Get(Guid photoId, string variant, [FromQuery] long exp, [FromQuery] string? sig, CancellationToken ct)
    {
        if (!Enum.TryParse<PhotoVariant>(variant, ignoreCase: true, out var v) || string.IsNullOrEmpty(sig))
            return NotFound();
        if (!signer.Verify(photoId, v, exp, sig, time.GetUtcNow()))
            return Problem("Link expired or invalid. Reload the page.", statusCode: StatusCodes.Status403Forbidden);

        var photo = await db.Photos.AsNoTracking()
            .Where(p => p.Id == photoId)
            .Select(p => new { p.StorageKeyThumb, p.StorageKeyWeb, p.StorageKeyOriginal, p.OriginalFileName })
            .FirstOrDefaultAsync(ct);
        var key = v switch
        {
            PhotoVariant.Thumb => photo?.StorageKeyThumb,
            PhotoVariant.Web => photo?.StorageKeyWeb,
            _ => photo?.StorageKeyOriginal,
        };
        if (key is null) return NotFound();

        var path = storage.GetPath(key);
        if (!System.IO.File.Exists(path)) return NotFound();

        // A photo's bytes never change for a given URL → let the browser cache until the URL expires.
        var maxAge = Math.Max(0, exp - time.GetUtcNow().ToUnixTimeSeconds());
        Response.Headers.CacheControl = $"private, max-age={maxAge}, immutable";

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
        return v == PhotoVariant.Original
            // Download name keeps the photographer's file name, with the real extension (HEIC was converted to .jpg).
            ? PhysicalFile(path, contentType, Path.ChangeExtension(photo!.OriginalFileName, Path.GetExtension(path)), enableRangeProcessing: true)
            : PhysicalFile(path, contentType, enableRangeProcessing: true);
    }
}
