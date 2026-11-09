using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Photos;

namespace ShaadiLens.Api.Controllers;

public sealed record PublishRequest(bool IsPublished);

[ApiController]
[Route("api/v1/events")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class EventsController(EventService events, PhotoService photos) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<EventSummaryDto>> List(CancellationToken ct) => events.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<EventDto> Get(Guid id, CancellationToken ct) => events.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<EventDto>> Create(SaveEventRequest req, CancellationToken ct)
    {
        var ev = await events.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = ev.Id }, ev);
    }

    [HttpPut("{id:guid}")]
    public Task<EventDto> Update(Guid id, SaveEventRequest req, CancellationToken ct) => events.UpdateAsync(id, req, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await events.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, PublishRequest req, CancellationToken ct)
    {
        await events.SetPublishedAsync(id, req.IsPublished, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/stats")]
    public Task<EventStatsDto> Stats(Guid id, CancellationToken ct) => events.GetStatsAsync(id, ct);

    /// <summary>Guest highlight reels on/off and photos per reel.</summary>
    [HttpPut("{id:guid}/reel-settings")]
    public async Task<IActionResult> ReelSettings(Guid id, Application.Reels.ReelSettingsDto req, CancellationToken ct)
    {
        await events.UpdateReelSettingsAsync(id, req, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/face-settings")]
    public async Task<IActionResult> FaceSettings(Guid id, FaceSettingsDto req, CancellationToken ct)
    {
        await events.UpdateFaceSettingsAsync(id, req, ct);
        return NoContent();
    }

    /// <summary>Threshold preview: how many matches at a given threshold (and a histogram for a slider).</summary>
    [HttpGet("{id:guid}/face-settings/preview")]
    public Task<ThresholdPreviewDto> PreviewThreshold(Guid id, [FromQuery] float? threshold, CancellationToken ct) =>
        events.PreviewThresholdAsync(id, threshold, ct);

    /// <summary>Search again for every guest (background job) — use after changing thresholds.</summary>
    [HttpPost("{id:guid}/rematch")]
    public async Task<IActionResult> Rematch(Guid id, CancellationToken ct)
    {
        await events.RematchAllAsync(id, ct);
        return Accepted();
    }

    // ---------- collections ----------

    [HttpPost("{id:guid}/collections")]
    public Task<CollectionDto> AddCollection(Guid id, SaveCollectionRequest req, CancellationToken ct) =>
        events.AddCollectionAsync(id, req, ct);

    [HttpPut("{id:guid}/collections/{collectionId:guid}")]
    public async Task<IActionResult> UpdateCollection(Guid id, Guid collectionId, SaveCollectionRequest req, CancellationToken ct)
    {
        await events.UpdateCollectionAsync(id, collectionId, req, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/collections/{collectionId:guid}")]
    public async Task<IActionResult> DeleteCollection(Guid id, Guid collectionId, CancellationToken ct)
    {
        await events.DeleteCollectionAsync(id, collectionId, ct);
        return NoContent();
    }

    /// <summary>Body: collection ids in the new order.</summary>
    [HttpPut("{id:guid}/collections/order")]
    public async Task<IActionResult> ReorderCollections(Guid id, List<Guid> orderedIds, CancellationToken ct)
    {
        await events.ReorderCollectionsAsync(id, orderedIds, ct);
        return NoContent();
    }

    // ---------- photos ----------

    [HttpGet("{id:guid}/photos")]
    public Task<Application.Common.PagedResult<PhotoDto>> Photos(Guid id, [FromQuery] PhotoQuery query, CancellationToken ct) =>
        photos.ListAsync(id, query, ct);

    [HttpPost("{id:guid}/photos/retry-failed")]
    public async Task<object> RetryFailed(Guid id, CancellationToken ct) => new { queued = await photos.RetryFailedAsync(id, ct) };

    /// <summary>Re-render web/thumb images with the current watermark setting (background jobs).</summary>
    [HttpPost("{id:guid}/photos/rebuild-images")]
    public async Task<object> RebuildImages(Guid id, CancellationToken ct) => new { queued = await photos.RebuildImagesAsync(id, ct) };

    // ---------- culling report ----------

    /// <summary>Counts for the Blurry / Eyes closed / Duplicates filter chips.</summary>
    [HttpGet("{id:guid}/photos/culling")]
    public Task<CullingSummaryDto> Culling(Guid id, [FromQuery] Guid? collectionId, CancellationToken ct) =>
        photos.GetCullingSummaryAsync(id, collectionId, ct);

    /// <summary>Run the culling analysis on photos processed before it existed (faces and matches are untouched).</summary>
    [HttpPost("{id:guid}/photos/analyze-quality")]
    public async Task<object> AnalyzeQuality(Guid id, CancellationToken ct) => new { queued = await photos.AnalyzeExistingAsync(id, ct) };

    /// <summary>Hide every near-duplicate except the keeper of its burst.</summary>
    [HttpPost("{id:guid}/photos/hide-burst-extras")]
    public async Task<object> HideBurstExtras(Guid id, [FromQuery] Guid? collectionId, CancellationToken ct) =>
        new { hidden = await photos.HideBurstExtrasAsync(id, collectionId, ct) };
}
