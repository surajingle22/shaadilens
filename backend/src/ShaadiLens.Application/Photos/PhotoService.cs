using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using Microsoft.Extensions.Options;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Photos;

public sealed record PhotoDto(
    Guid Id, Guid CollectionId, string FileName, int Width, int Height, DateTimeOffset? TakenAt,
    PhotoStatus Status, int FaceCount, bool IsHighlight, bool IsHidden, DateTimeOffset UploadedAt,
    string? Error, string? ThumbUrl, string? WebUrl,
    // Culling report
    bool Analysed, bool IsBlurry, int ClosedEyes, Guid? BurstGroupId, bool IsBurstBest);

public sealed record FaceBoxDto(Guid Id, float X, float Y, float W, float H, float DetScore, float FaceSizePx, float Yaw, float Quality, float? EyeAspectRatio);

public sealed record PhotoDetailDto(PhotoDto Photo, string? CameraModel, IReadOnlyList<FaceBoxDto> Faces);

/// <summary>Admin photo-grid filters. Culling filters only see photos whose analysis is done.</summary>
public enum PhotoFilter { All, Blurry, EyesClosed, Duplicates, Highlights, Hidden }

public sealed record PhotoQuery(Guid? CollectionId, PhotoStatus? Status, PhotoFilter Filter = PhotoFilter.All, int Page = 1, int PageSize = 60);

/// <summary>Numbers for the filter chips. BurstExtras = photos you could hide (every burst minus its keeper).</summary>
public sealed record CullingSummaryDto(int Processed, int NotAnalysed, int Blurry, int EyesClosed, int BurstGroups, int BurstExtras, int Highlights, int Hidden);

public sealed record UpdatePhotoRequest(bool? IsHighlight, bool? IsHidden, Guid? CollectionId);

/// <summary>Admin use cases for photos.</summary>
public sealed class PhotoService(
    IAppDbContext db, IStorageService storage, IPhotoPipelineScheduler pipeline, IMediaUrlSigner urls, IOptions<CullingOptions> culling)
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
        query = ApplyFilter(query, q.Filter);

        var total = await query.CountAsync(ct);
        // Photos sorted by when they were taken (EXIF), falling back to upload time.
        // Bursts are shown group by group, keeper first.
        var ordered = q.Filter == PhotoFilter.Duplicates
            ? query.OrderBy(p => p.BurstGroupId).ThenByDescending(p => p.IsBurstBest).ThenBy(p => p.TakenAt ?? p.UploadedAt).ThenBy(p => p.Id)
            : query.OrderBy(p => p.TakenAt ?? p.UploadedAt).ThenBy(p => p.Id);
        var items = await ordered
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<PhotoDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    public async Task<PhotoDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Photos.AsNoTracking().Include(x => x.Faces).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Photo");
        var faces = p.Faces.OrderByDescending(f => f.FaceSizePx)
            .Select(f => new FaceBoxDto(f.Id, f.BoxX, f.BoxY, f.BoxW, f.BoxH, f.DetScore, f.FaceSizePx, f.Yaw, f.QualityScore, f.EyeAspectRatio))
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
        if (p.BurstGroupId is { } group) // re-pick the keeper (or dissolve a burst of one)
        {
            Culling.PickBest(await db.Photos.Where(x => x.BurstGroupId == group).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
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

    /// <summary>Re-render web + thumb of every processed photo (after the watermark was changed).</summary>
    public async Task<int> RebuildImagesAsync(Guid eventId, CancellationToken ct)
    {
        var ids = await db.Photos.Where(p => p.EventId == eventId && p.StorageKeyWeb != null).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in ids) pipeline.EnqueueImageRebuild(id);
        return ids.Count;
    }

    // ---------------- culling report ----------------

    private IQueryable<Domain.Entities.Photo> ApplyFilter(IQueryable<Domain.Entities.Photo> query, PhotoFilter filter)
    {
        var o = culling.Value;
        float faceBelow = o.BlurryFaceBelow, imageBelow = o.BlurryImageBelow;
        return filter switch
        {
            // With people: judge the sharpest face (backgrounds are blurred on purpose). Without: the whole photo.
            PhotoFilter.Blurry => query.Where(p => p.PerceptualHash != null
                && (p.FaceCount > 0 ? p.BlurScore < faceBelow : p.Sharpness < imageBelow)),
            PhotoFilter.EyesClosed => query.Where(p => p.ClosedEyesCount > 0),
            PhotoFilter.Duplicates => query.Where(p => p.BurstGroupId != null),
            PhotoFilter.Highlights => query.Where(p => p.IsHighlight),
            PhotoFilter.Hidden => query.Where(p => p.IsHidden),
            _ => query,
        };
    }

    public async Task<CullingSummaryDto> GetCullingSummaryAsync(Guid eventId, Guid? collectionId, CancellationToken ct)
    {
        var all = db.Photos.AsNoTracking().Where(p => p.EventId == eventId
            && (p.Status == PhotoStatus.FacesDetected || p.Status == PhotoStatus.Matched));
        if (collectionId is { } cid) all = all.Where(p => p.CollectionId == cid);

        var bursts = await all.Where(p => p.BurstGroupId != null).GroupBy(p => p.BurstGroupId).Select(g => g.Count()).ToListAsync(ct);
        return new CullingSummaryDto(
            Processed: await all.CountAsync(ct),
            NotAnalysed: await all.CountAsync(p => p.PerceptualHash == null, ct),
            Blurry: await ApplyFilter(all, PhotoFilter.Blurry).CountAsync(ct),
            EyesClosed: await ApplyFilter(all, PhotoFilter.EyesClosed).CountAsync(ct),
            BurstGroups: bursts.Count,
            BurstExtras: bursts.Sum(n => n - 1),
            Highlights: await all.CountAsync(p => p.IsHighlight, ct),
            Hidden: await all.CountAsync(p => p.IsHidden, ct));
    }

    /// <summary>Queue the culling analysis for processed photos that don't have it yet (uploaded before Phase 3).</summary>
    public async Task<int> AnalyzeExistingAsync(Guid eventId, CancellationToken ct)
    {
        var ids = await db.Photos.Where(p => p.EventId == eventId && p.PerceptualHash == null
                && (p.Status == PhotoStatus.FacesDetected || p.Status == PhotoStatus.Matched))
            .Select(p => p.Id).ToListAsync(ct);
        foreach (var id in ids) pipeline.EnqueueQualityAnalysis(id);
        return ids.Count;
    }

    /// <summary>"Keep best, hide the rest": hide every burst photo except its keeper. Returns how many were hidden.</summary>
    public async Task<int> HideBurstExtrasAsync(Guid eventId, Guid? collectionId, CancellationToken ct)
    {
        var q = db.Photos.Where(p => p.EventId == eventId && p.BurstGroupId != null && !p.IsBurstBest && !p.IsHidden);
        if (collectionId is { } cid) q = q.Where(p => p.CollectionId == cid);
        return await q.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsHidden, true), ct);
    }

    /// <summary>The admin prefers this frame of its burst: it becomes the keeper (and is shown again if hidden).</summary>
    public async Task KeepInBurstAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Photos.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Photo");
        if (p.BurstGroupId is not { } group) throw new BadRequestException("This photo isn't part of a burst");
        var members = await db.Photos.Where(x => x.BurstGroupId == group).ToListAsync(ct);
        foreach (var m in members) m.BurstPickedByAdmin = m.Id == id;
        Culling.PickBest(members);
        p.IsHidden = false;
        await db.SaveChangesAsync(ct);
    }

    private PhotoDto ToDto(Domain.Entities.Photo p) => new(
        p.Id, p.CollectionId, p.OriginalFileName, p.Width, p.Height, p.TakenAt, p.Status, p.FaceCount,
        p.IsHighlight, p.IsHidden, p.UploadedAt, p.Error,
        p.StorageKeyThumb is null ? null : urls.GetUrl(p.Id, PhotoVariant.Thumb),
        p.StorageKeyWeb is null ? null : urls.GetUrl(p.Id, PhotoVariant.Web),
        p.PerceptualHash is not null, p.PerceptualHash is not null && Culling.IsBlurry(p, culling.Value), p.ClosedEyesCount,
        p.BurstGroupId, p.IsBurstBest);
}
