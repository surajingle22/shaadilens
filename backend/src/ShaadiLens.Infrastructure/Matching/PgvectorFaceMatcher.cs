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
/// Face matching with pgvector, in both directions:
///   forward (SearchForGuestAsync) — one guest × all faces: selfie search, "find more", threshold changes
///   reverse (MatchPhotoAsync)     — one new photo × all guests: photos uploaded later reach guests by themselves
///
/// Forward search, step by step:
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

    // ---------------------------------------------------------------------------------------------
    // Reverse search: one new photo × every guest
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs after face detection of every photo:
    ///     for each face in the photo:
    ///         SELECT guest_id, embedding &lt;=&gt; @face FROM guest_references (of this event) WHERE distance ≤ 1 − Probable
    /// keep the best similarity per guest, then write one GuestMatch per (guest, photo) — same rules as the
    /// forward search. Cost: (#faces in photo) small queries over (#guest references), i.e. a few ms.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> MatchPhotoAsync(Guid photoId, IReadOnlyDictionary<Guid, MatchStatus>? decisions = null, CancellationToken ct = default)
    {
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo is null) return [];
        var ev = await db.Events.AsNoTracking().Where(e => e.Id == photo.EventId)
            .Select(e => new { e.FaceSettings, e.FaceSearchEnabled }).FirstAsync(ct);
        var settings = ev.FaceSettings;
        var maxDistance = 1.0 - settings.ProbableThreshold;

        // guestId → best face of this photo for that guest
        var bestPerGuest = new Dictionary<Guid, (Guid FaceId, float Similarity)>();
        if (ev.FaceSearchEnabled)
        {
            var faces = await db.Faces.AsNoTracking().Where(f => f.PhotoId == photoId)
                .Select(f => new { f.Id, f.Embedding }).ToListAsync(ct);
            foreach (var face in faces)
            {
                var hits = await db.GuestReferences.AsNoTracking()
                    .Where(r => db.Guests.Any(g => g.Id == r.GuestId && g.EventId == photo.EventId)
                                && r.Embedding.CosineDistance(face.Embedding) <= maxDistance)
                    .Select(r => new { r.GuestId, Distance = r.Embedding.CosineDistance(face.Embedding) })
                    .ToListAsync(ct);
                foreach (var hit in hits)
                {
                    var similarity = (float)(1.0 - hit.Distance);
                    if (!bestPerGuest.TryGetValue(hit.GuestId, out var current) || similarity > current.Similarity)
                        bestPerGuest[hit.GuestId] = (face.Id, similarity);
                }
            }
        }

        var existing = await db.GuestMatches.Where(m => m.PhotoId == photoId).ToDictionaryAsync(m => m.GuestId, ct);
        var now = time.GetUtcNow();
        var newlyVisible = new List<Guid>();

        foreach (var (guestId, best) in bestPerGuest)
        {
            var status = MatchStatus.Probable;
            var restored = decisions is not null && decisions.TryGetValue(guestId, out status);
            if (!restored) status = best.Similarity >= settings.MatchThreshold ? MatchStatus.Auto : MatchStatus.Probable;

            if (existing.Remove(guestId, out var match))
            {
                if (match.Status is MatchStatus.Confirmed or MatchStatus.Rejected) continue; // guest's decision wins
                if (status == MatchStatus.Auto && match.Status != MatchStatus.Auto) newlyVisible.Add(guestId);
                match.FaceId = best.FaceId;
                match.Score = best.Similarity;
                match.Status = status;
            }
            else
            {
                db.GuestMatches.Add(new GuestMatch
                {
                    GuestId = guestId, PhotoId = photoId, FaceId = best.FaceId,
                    Score = best.Similarity, Status = status, CreatedAt = now,
                });
                if (status == MatchStatus.Auto && !restored) newlyVisible.Add(guestId);
            }
        }
        db.GuestMatches.RemoveRange(existing.Values.Where(m => m.Status is MatchStatus.Auto or MatchStatus.Probable));

        if (photo.Status == PhotoStatus.FacesDetected) photo.Status = PhotoStatus.Matched;
        await db.SaveChangesAsync(ct);

        if (bestPerGuest.Count > 0)
            log.LogInformation("Reverse match photo {PhotoId}: {Guests} guest(s) matched, {New} with a new photo",
                photoId, bestPerGuest.Count, newlyVisible.Count);
        return newlyVisible;
    }

    // ---------------------------------------------------------------------------------------------
    // Threshold preview
    // ---------------------------------------------------------------------------------------------

    /// <summary>Lowest similarity we look at for the preview (anything below is never a useful threshold).</summary>
    public const int HistogramMinBucket = 20;

    /// <summary>
    /// For every (guest, photo) pair: the best similarity over the guest's references and the photo's faces,
    /// counted in 0.01-wide buckets. With that the UI can answer "how many matches at 0.40? at 0.45?" for any
    /// slider position without asking the server again.
    ///
    /// This compares ALL references with ALL faces of the event (500 guests × 2 refs × 15k faces ≈ 15M
    /// distances), so it takes a few seconds — it's a button in the admin, not something run per request.
    /// </summary>
    public async Task<ThresholdHistogram> HistogramAsync(Guid eventId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var maxDistance = 1.0 - HistogramMinBucket / 100.0;
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));

        var rows = await db.Database.SqlQuery<BucketRow>($"""
            SELECT LEAST(floor(x.sim * 100), 100)::int AS "Bucket", count(*)::int AS "Count"
            FROM (
                SELECT r."GuestId", f."PhotoId", max(1 - (f."Embedding" <=> r."Embedding")) AS sim
                FROM "GuestReferences" r
                JOIN "Guests" g ON g."Id" = r."GuestId"
                JOIN "Faces" f ON f."EventId" = g."EventId"
                WHERE g."EventId" = {eventId} AND (f."Embedding" <=> r."Embedding") <= {maxDistance}
                GROUP BY r."GuestId", f."PhotoId"
            ) x
            JOIN "Photos" p ON p."Id" = x."PhotoId" AND NOT p."IsHidden"
            WHERE NOT EXISTS (
                SELECT 1 FROM "GuestMatches" m
                WHERE m."GuestId" = x."GuestId" AND m."PhotoId" = x."PhotoId" AND m."Status" IN (2, 3))
            GROUP BY 1
            """).ToListAsync(ct);

        var decided = await db.GuestMatches.AsNoTracking()
            .Where(m => db.Guests.Any(g => g.Id == m.GuestId && g.EventId == eventId)
                        && (m.Status == MatchStatus.Confirmed || m.Status == MatchStatus.Rejected))
            .GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var guests = await db.Guests.CountAsync(g => g.EventId == eventId && g.References.Any(), ct);

        log.LogInformation("Threshold histogram for event {EventId}: {Guests} guests in {Ms} ms", eventId, guests, sw.ElapsedMilliseconds);
        return new ThresholdHistogram(HistogramMinBucket, rows.ToDictionary(r => r.Bucket, r => r.Count),
            decided.GetValueOrDefault(MatchStatus.Confirmed), decided.GetValueOrDefault(MatchStatus.Rejected),
            guests, sw.Elapsed.TotalMilliseconds);
    }

    private sealed class BucketRow
    {
        public int Bucket { get; set; }
        public int Count { get; set; }
    }
}
