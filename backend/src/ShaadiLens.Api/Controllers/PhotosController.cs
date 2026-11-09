using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Photos;

namespace ShaadiLens.Api.Controllers;

[ApiController]
[Route("api/v1/photos")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class PhotosController(PhotoService photos) : ControllerBase
{
    /// <summary>Photo with its face boxes (for the admin face overlay).</summary>
    [HttpGet("{id:guid}")]
    public Task<PhotoDetailDto> Get(Guid id, CancellationToken ct) => photos.GetAsync(id, ct);

    /// <summary>Hide, highlight or move to another collection. Omitted fields are unchanged.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdatePhotoRequest req, CancellationToken ct)
    {
        await photos.UpdateAsync(id, req, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await photos.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Make this frame the keeper of its burst (near-duplicate group).</summary>
    [HttpPost("{id:guid}/keep-in-burst")]
    public async Task<IActionResult> KeepInBurst(Guid id, CancellationToken ct)
    {
        await photos.KeepInBurstAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reprocess")]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        await photos.ReprocessAsync(id, ct);
        return Accepted();
    }
}
