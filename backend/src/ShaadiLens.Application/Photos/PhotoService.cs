using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Photos;

public sealed record PhotoDto(
    Guid Id, Guid CollectionId, string FileName, int Width, int Height, DateTimeOffset? TakenAt,
    PhotoStatus Status, int FaceCount, bool IsHighlight, bool IsHidden, DateTimeOffset UploadedAt,
    string? Error, string? ThumbUrl, string? WebUrl);

public sealed record FaceBoxDto(Guid Id, float X, float Y, float W, float H, float DetScore, float FaceSizePx, float Yaw, float Quality);

public sealed record PhotoDetailDto(PhotoDto Photo, string? CameraModel, IReadOnlyList<FaceBoxDto> Faces);

public sealed record PhotoQuery(Guid? CollectionId, PhotoStatus? Status, int Page = 1, int PageSize = 60);

public sealed record UpdatePhotoRequest(bool? IsHighlight, bool? IsHidden, Guid? CollectionId);

/// <summary>Admin use cases for photos.</summary>
public sealed class PhotoService(IAppDbContext db, IStorageService storage, IPhotoPipelineScheduler pipeline, IMediaUrlSigner urls)
{
    public async Task<PagedResult<PhotoDto>> ListAsync(Guid eventId, PhotoQuery q, CancellationToken ct)
    {
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var page = Math.Max(1, q.Page);

        var query = db.Photos.AsNoTracking().Where(p => p.EventId == eventId);
        if (q.CollectionId is { } cid) query = query.Where(p => p.CollectionId == cid);
        // "Done" = faces detected (and matched against guests, the last step).
        if (q.Status is PhotoStatus.FacesDetected or PhotoStatus.Matched)
            query = query.Where(p => p.Status == PhotoStatus.FacesDetected || p.Status == PhotoStatus.Matched);
        else if (q.Status is { } st) query = query.Where(p => p.Status == st);

        var total = await query.CountAsync(ct);
        // Photos sorted by when they were taken (EXIF), falling back to upload time.
        var items = await query
            .OrderBy(p => p.TakenAt ?? p.UploadedAt).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<PhotoDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    public async Task<PhotoDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Photos.AsNoTracking().Include(x => x.Faces).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Photo");
        var faces = p.Faces.OrderByDescending(f => f.FaceSizePx)
            .Select(f => new FaceBoxDto(f.Id, f.BoxX, f.BoxY, f.BoxW, f.BoxH, f.DetScore, f.FaceSizePx, f.Yaw, f.QualityScore))
            .ToList();
        return new PhotoDetailDto(ToDto(p), p.CameraModel, faces);
    }

    public async Task UpdateAsync(Guid id, UpdatePhotoRequest req, CancellationToken ct)
    {
        var p = await db.Photos.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Photo");
        if (req.IsHighlight is { } h) p.IsHighlight = h;
        if (req.IsHidden is { } hidden) p.IsHidden = hidden;
        if (req.CollectionId is { } cid && cid != p.CollectionId)
        {
            if (!await db.Collections.AnyAsync(c => c.Id == cid && c.EventId == p.EventId, ct))
                throw new BadRequestException("Target collection is not part of this event");
            p.CollectionId = cid;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Photos.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Photo");
        db.Photos.Remove(p); // faces + guest matches cascade
        await db.SaveChangesAsync(ct);
        await storage.DeletePrefixAsync(StorageKeys.PhotoFolder(p.EventId, p.Id), ct);
    }

    /// <summary>Re-run the pipeline. If the resized images exist, only face detection runs again.</summary>
    public async Task ReprocessAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Photos.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Photo");
        p.Error = null;
        if (p.StorageKeyWeb is not null)
        {
            p.Status = PhotoStatus.Resized;
            await db.SaveChangesAsync(ct);
            pipeline.EnqueueFaceDetection(p.Id);
        }
        else
        {
            p.Status = PhotoStatus.Uploaded;
            await db.SaveChangesAsync(ct);
            pipeline.EnqueueProcessing(p.Id);
        }
    }

    /// <summary>Re-queue every failed photo of an event.</summary>
    public async Task<int> RetryFailedAsync(Guid eventId, CancellationToken ct)
    {
        var ids = await db.Photos.Where(p => p.EventId == eventId && p.Status == PhotoStatus.Failed).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in ids) await ReprocessAsync(id, ct);
        return ids.Count;
    }

    private PhotoDto ToDto(Domain.Entities.Photo p) => new(
        p.Id, p.CollectionId, p.OriginalFileName, p.Width, p.Height, p.TakenAt, p.Status, p.FaceCount,
        p.IsHighlight, p.IsHidden, p.UploadedAt, p.Error,
        p.StorageKeyThumb is null ? null : urls.GetUrl(p.Id, PhotoVariant.Thumb),
        p.StorageKeyWeb is null ? null : urls.GetUrl(p.Id, PhotoVariant.Web));
}
