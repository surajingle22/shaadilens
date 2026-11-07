using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;

namespace ShaadiLens.Infrastructure.Matching;

/// <summary>
/// Selfie search ("forward search") with pgvector.
///
/// For each of the guest's reference embeddings (selfie, extra selfies, confirmed faces):
///     SELECT id, photo_id, embedding &lt;=&gt; @ref AS distance FROM faces
///     WHERE event_id = @event AND embedding &lt;=&gt; @ref &lt;= (1 - ProbableThreshold)
/// Cosine distance = 1 − cosine similarity, so "distance ≤ 1 − 0.34" means "similarity ≥ 0.34".
///
/// Then:
///   1. per face, keep the BEST similarity over all references  (query expansion: "find more")
///   2. per photo, keep the best face                          (one row per guest × photo)
///   3. similarity ≥ MatchThreshold → Auto, else → Probable
///   4. the guest's own decisions win: Confirmed / Rejected rows are never changed
///   5. old Auto/Probable rows that no longer qualify (e.g. threshold raised) are removed
///
/// Why an exact scan instead of the HNSW index? HNSW is approximate: with a WHERE filter it can return fewer
/// results than exist. At wedding scale (≤ ~100k faces) the exact scan costs tens of milliseconds and never
/// misses a match — the right trade-off here. The HNSW index serves "nearest N" queries (ORDER BY … LIMIT).
/// </summary>
public sealed class PgvectorFaceMatcher(AppDbContext db, TimeProvider time, ILogger<PgvectorFaceMatcher> log) : IFaceMatcher
{
    /// <summary>Safety cap per reference; no real guest appears in more faces than this.</summary>
    public const int MaxCandidatesPerReference = 5000;

    public async Task<MatchSummary> SearchForGuestAsync(Guid guestId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var guest = await db.Guests.Include(g => g.References).FirstAsync(g => g.Id == guestId, ct);
        var settings = await db.Events.AsNoTracking().Where(e => e.Id == guest.EventId).Select(e => e.FaceSettings).FirstAsync(ct);
        var maxDistance = 1.0 - settings.ProbableThreshold;

        // faceId → (photoId, best similarity over all references)
        var bestPerFace = new Dictionary<Guid, (Guid PhotoId, float Similarity)>();
        foreach (var reference in guest.References)
        {
            var hits = await db.Faces.AsNoTracking()
                .Where(f => f.EventId == guest.EventId && f.Embedding.CosineDistance(reference.Embedding) <= maxDistance)
                .Select(f => new { f.Id, f.PhotoId, Distance = f.Embedding.CosineDistance(reference.Embedding) })
                .Take(MaxCandidatesPerReference)
                .ToListAsync(ct);

            foreach (var hit in hits)
            {
                var similarity = (float)(1.0 - hit.Distance);
                if (!bestPerFace.TryGetValue(hit.Id, out var current) || similarity > current.Similarity)
                    bestPerFace[hit.Id] = (hit.PhotoId, similarity);
            }
        }

        // photoId → best face in that photo
        var bestPerPhoto = bestPerFace
            .GroupBy(kv => kv.Value.PhotoId)
            .ToDictionary(g => g.Key, g => g.MaxBy(kv => kv.Value.Similarity));

        var existing = await db.GuestMatches.Where(m => m.GuestId == guestId).ToDictionaryAsync(m => m.PhotoId, ct);
        var now = time.GetUtcNow();
        int auto = 0, probable = 0;

        foreach (var (photoId, best) in bestPerPhoto)
        {
            var status = best.Value.Similarity >= settings.MatchThreshold ? MatchStatus.Auto : MatchStatus.Probable;
            if (existing.Remove(photoId, out var match))
            {
                if (match.Status is MatchStatus.Confirmed or MatchStatus.Rejected) continue; // guest's decision wins
                match.FaceId = best.Key;
                match.Score = best.Value.Similarity;
                match.Status = status;
            }
            else
            {
                db.GuestMatches.Add(new GuestMatch
                {
                    GuestId = guestId, PhotoId = photoId, FaceId = best.Key,
                    Score = best.Value.Similarity, Status = status, CreatedAt = now,
                });
            }
            if (status == MatchStatus.Auto) auto++; else probable++;
        }

        // Whatever is left in `existing` didn't match this time. Drop automatic rows; keep the guest's decisions.
        db.GuestMatches.RemoveRange(existing.Values.Where(m => m.Status is MatchStatus.Auto or MatchStatus.Probable));

        guest.LastSearchedAt = now;
        await db.SaveChangesAsync(ct);

        var facesCompared = await db.Faces.CountAsync(f => f.EventId == guest.EventId, ct);
        log.LogInformation("Face search for guest {GuestId}: {References} reference(s) × {Faces} faces → {Auto} auto, {Probable} probable in {Ms} ms",
            guestId, guest.References.Count, facesCompared, auto, probable, sw.ElapsedMilliseconds);
        return new MatchSummary(auto, probable, facesCompared, sw.Elapsed.TotalMilliseconds);
    }
}
