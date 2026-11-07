using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Uploads;

namespace ShaadiLens.Api.Controllers;

/// <summary>Chunked, resumable photo upload. See UploadService for the protocol.</summary>
[ApiController]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class UploadsController(UploadService uploads) : ControllerBase
{
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
