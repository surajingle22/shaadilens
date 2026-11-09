using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Photos;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Infrastructure.Persistence;

namespace ShaadiLens.Infrastructure.Processing;

/// <summary>
/// Puts a photo into a "burst" group with its near-duplicates (photographers often shoot 3–10 frames of the same
/// moment). Two photos are near-duplicates when they are in the same collection, were taken within
/// <see cref="CullingOptions.BurstWindowSeconds"/> of each other, and their 64-bit perceptual hashes differ in at
/// most <see cref="CullingOptions.DuplicateMaxBits"/> bits.
///
/// Grouping is transitive: if A≈B and B≈C, all three end up in one group (groups are merged).
/// Runs right after face detection, one photo at a time. Several face workers run in parallel, so a Postgres
/// advisory lock per collection makes sure two photos of the same burst never create two separate groups.
/// </summary>
public sealed class BurstGrouper(AppDbContext db, IOptions<CullingOptions> options)
{
    public async Task AssignAsync(Guid photoId, CancellationToken ct)
    {
        var o = options.Value;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo is null) return;
        var lockKey = photo.CollectionId.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 42))", ct);

        // Re-processing: leave the old group first (the photo may have been moved or re-hashed).
        var oldGroup = photo.BurstGroupId;
        var wasPicked = photo.BurstPickedByAdmin;
        photo.BurstGroupId = null;
        photo.IsBurstBest = false;
        photo.BurstPickedByAdmin = false;

        if (photo.PerceptualHash is { } hash)
        {
            var window = TimeSpan.FromSeconds(o.BurstWindowSeconds);
            var query = db.Photos.Where(p => p.CollectionId == photo.CollectionId && p.Id != photo.Id && p.PerceptualHash != null);
            if (photo.TakenAt is { } t)
            {
                var from = t - window;
                var to = t + window;
                query = query.Where(p => p.TakenAt == null || (p.TakenAt >= from && p.TakenAt <= to));
            }
            var candidates = await query.Select(p => new { p.Id, p.PerceptualHash, p.BurstGroupId }).ToListAsync(ct);
            var similar = candidates.Where(c => Culling.Distance(hash, c.PerceptualHash!.Value) <= o.DuplicateMaxBits).ToList();

            if (similar.Count > 0)
            {
                var groups = similar.Select(c => c.BurstGroupId).OfType<Guid>().Distinct().ToList();
                var target = groups.Count > 0 ? groups[0] : Guid.CreateVersion7();
                var ids = similar.Select(c => c.Id).ToList();
                // Everyone similar + everyone in the groups being merged → one group.
                await db.Photos.Where(p => ids.Contains(p.Id) || (p.BurstGroupId != null && groups.Contains(p.BurstGroupId.Value)))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.BurstGroupId, target), ct);
                photo.BurstGroupId = target;
                photo.BurstPickedByAdmin = wasPicked && target == oldGroup; // re-analysed, same burst: keep the admin's pick
            }
        }
        await db.SaveChangesAsync(ct);

        if (oldGroup is { } old && old != photo.BurstGroupId) await RefreshAsync(old, ct);
        if (photo.BurstGroupId is { } now) await RefreshAsync(now, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Re-pick the best photo of a group: the admin's own pick if there is one, otherwise eyes open + sharpest.
    /// A "group" of one photo isn't a burst any more: dissolve it.
    /// </summary>
    public async Task RefreshAsync(Guid groupId, CancellationToken ct)
    {
        Culling.PickBest(await db.Photos.Where(p => p.BurstGroupId == groupId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
