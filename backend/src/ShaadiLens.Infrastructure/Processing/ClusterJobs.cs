using System.Diagnostics;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShaadiLens.Application.Faces;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Infrastructure.Persistence;

namespace ShaadiLens.Infrastructure.Processing;

/// <summary>
/// "People" view: groups every face of an event into Person 1, Person 2… with DBSCAN (in face-service).
///
///   1. load all face embeddings of the event       (15k faces × 512 floats ≈ 30 MB)
///   2. face-service /v1/cluster → one label per face (-1 = not grouped)
///   3. replace the event's clusters; Person 1 = most faces
///   4. keep the admin's work: a new cluster inherits the name / linked guest of the old cluster that most
///      of its faces were in (clusters shift a little when photos are added and the job runs again)
/// </summary>
public sealed class ClusterJobs(AppDbContext db, IFaceEngine engine, TimeProvider time, ILogger<ClusterJobs> log)
{
    /// <summary>Cosine distance: faces closer than this are neighbours (similarity ≥ 0.5).</summary>
    public const float DefaultEps = 0.5f;
    /// <summary>A person needs at least this many faces to become a cluster.</summary>
    public const int DefaultMinSamples = 3;

    [Queue("faces")]
    [AutomaticRetry(Attempts = 1)]
    public async Task RunAsync(Guid eventId, float eps, int minSamples, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return;

        var faces = await db.Faces.AsNoTracking().Where(f => f.EventId == eventId).OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.PhotoId, f.Embedding, f.ClusterId }).ToListAsync(ct);
        var labels = await engine.ClusterAsync(faces.Select(f => f.Embedding.ToArray()).ToList(), eps, minSamples, ct);

        var old = await db.PersonClusters.Where(c => c.EventId == eventId).ToDictionaryAsync(c => c.Id, ct);
        var groups = faces.Select((f, i) => (Face: f, Label: labels[i]))
            .Where(x => x.Label >= 0)
            .GroupBy(x => x.Label)
            .OrderByDescending(g => g.Count())
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PersonClusters.RemoveRange(old.Values); // faces' ClusterId → NULL (FK ON DELETE SET NULL)
        await db.SaveChangesAsync(ct);

        var inherited = new HashSet<Guid>();
        var now = time.GetUtcNow();
        var number = 0;
        foreach (var group in groups)
        {
            var cluster = new PersonCluster
            {
                EventId = eventId, Number = ++number, CreatedAt = now,
                FaceCount = group.Count(), PhotoCount = group.Select(x => x.Face.PhotoId).Distinct().Count(),
            };
            // Majority vote: which old cluster were most of these faces in?
            var previous = group.Where(x => x.Face.ClusterId is not null)
                .GroupBy(x => x.Face.ClusterId!.Value)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (previous is not null && previous.Count() * 2 >= group.Count() &&
                old.TryGetValue(previous.Key, out var was) && inherited.Add(was.Id))
            {
                cluster.Name = was.Name;
                cluster.GuestId = was.GuestId;
            }
            db.PersonClusters.Add(cluster);
            await db.SaveChangesAsync(ct);

            foreach (var chunk in group.Select(x => x.Face.Id).Chunk(5000))
                await db.Faces.Where(f => chunk.Contains(f.Id)).ExecuteUpdateAsync(s => s.SetProperty(f => f.ClusterId, cluster.Id), ct);
        }

        ev.ClustersUpdatedAt = time.GetUtcNow();
        ev.ClusteringStartedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Clustered {Faces} faces of event {EventId} into {People} people ({Noise} ungrouped) in {Ms} ms",
            faces.Count, eventId, groups.Count, labels.Count(l => l < 0), sw.ElapsedMilliseconds);
    }
}
