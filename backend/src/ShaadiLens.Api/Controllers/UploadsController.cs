using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Uploads;

namespace ShaadiLens.Api.Controllers;

public sealed record UploadCollection(Guid Id, string Name);
public sealed record UploadTarget(Guid EventId, string Name, string Slug, IReadOnlyList<UploadCollection> Collections);

/// <summary>Chunked, resumable photo upload (admin, or the hot-folder uploader with its API key). See UploadService.</summary>
[ApiController]
[Authorize(Policy = AuthConstants.UploadPolicy)]
public sealed class UploadsController(UploadService uploads) : ControllerBase
{
    /// <summary>For the hot-folder uploader: events and their collections, so it can find "Wedding" by name.</summary>
    [HttpGet("api/v1/uploader/targets")]
    public async Task<IReadOnlyList<UploadTarget>> Targets([FromServices] Application.Events.EventService events, CancellationToken ct)
    {
        var list = new List<UploadTarget>();
        foreach (var e in await events.ListAsync(ct))
        {
            var detail = await events.GetAsync(e.Id, ct);
            list.Add(new UploadTarget(e.Id, e.Name, e.Slug, detail.Collections.Select(c => new UploadCollection(c.Id, c.Name)).ToList()));
        }
        return list;
    }

    [HttpPost("api/v1/events/{eventId:guid}/uploads")]
    public Task<InitUploadResponse> Init(Guid eventId, InitUploadRequest req, CancellationToken ct) =>
        uploads.InitAsync(eventId, req, ct);

    /// <summary>Raw bytes of one chunk in the body (Content-Type: application/octet-stream).</summary>
    [HttpPut("api/v1/uploads/{uploadId:guid}/chunks/{index:int}")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> PutChunk(Guid uploadId, int index, CancellationToken ct)
    {
        await uploads.PutChunkAsync(uploadId, index, Request.Body, ct);
        return NoContent();
    }

    [HttpGet("api/v1/uploads/{uploadId:guid}")]
    public Task<UploadStatusResponse> Status(Guid uploadId, CancellationToken ct) => uploads.GetStatusAsync(uploadId, ct);

    [HttpPost("api/v1/uploads/{uploadId:guid}/complete")]
    public Task<CompleteUploadResponse> Complete(Guid uploadId, CancellationToken ct) => uploads.CompleteAsync(uploadId, ct);
}
