using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.People;

public sealed record FaceSampleDto(Guid PhotoId, string ThumbUrl, string WebUrl, int Width, int Height, FaceBoxDto Face);

public sealed record PersonDto(
    Guid Id, int Number, string? Name, Guid? GuestId, string? GuestName, int FaceCount, int PhotoCount,
    IReadOnlyList<FaceSampleDto> Samples);

/// <param name="Running">A clustering job is queued or running.</param>
public sealed record PeopleDto(DateTimeOffset? UpdatedAt, bool Running, int Faces, int GroupedFaces, IReadOnlyList<PersonDto> People);

public sealed record RunClusteringRequest(float? Eps, int? MinSamples);

/// <param name="GuestId">Link the person to a guest (null = unlink).</param>
public sealed record UpdatePersonRequest(string? Name, Guid? GuestId);

public sealed class RunClusteringRequestValidator : AbstractValidator<RunClusteringRequest>
{
    public RunClusteringRequestValidator()
    {
        RuleFor(x => x.Eps).InclusiveBetween(0.2f, 0.8f).When(x => x.Eps is not null);
        RuleFor(x => x.MinSamples).InclusiveBetween(2, 20).When(x => x.MinSamples is not null);
    }
}

/// <summary>
/// Admin "People" page: Person 1, Person 2… found by clustering. Naming a person is just a label; linking
/// a person to a guest gives that guest all the person's photos:
///   - every photo of the cluster becomes a Confirmed match (admin-reviewed), except ones the guest rejected
///   - up to 10 of the cluster's biggest faces become references, so later uploads and "Find more" use them
/// </summary>
public sealed class PeopleService(
    IAppDbContext db, IPhotoPipelineScheduler scheduler, IFaceMatcher matcher, IMediaUrlSigner urls, TimeProvider time)
{
    public const int SamplesPerPerson = 8;
    public const int ReferencesPerLink = 10;

    public async Task<PeopleDto> GetAsync(Guid eventId, CancellationToken ct)
    {
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw new NotFoundException("Event");
        var clusters = await db.PersonClusters.AsNoTracking().Where(c => c.EventId == eventId).OrderBy(c => c.Number).ToListAsync(ct);
        var guestNames = await db.Guests.AsNoTracking().Where(g => g.EventId == eventId)
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        // The biggest (= clearest) faces of each person, for the face strip.
        var ids = clusters.Select(c => c.Id).ToList();
        var faces = await db.Faces.AsNoTracking()
            .Where(f => f.ClusterId != null && ids.Contains(f.ClusterId.Value))
            .OrderByDescending(f => f.FaceSizePx)
            .Select(f => new { ClusterId = f.ClusterId!.Value, f.PhotoId, f.BoxX, f.BoxY, f.BoxW, f.BoxH, f.Photo!.Width, f.Photo.Height })
            .ToListAsync(ct);
        var samples = faces.GroupBy(f => f.ClusterId).ToDictionary(g => g.Key, g => g
            .DistinctBy(f => f.PhotoId).Take(SamplesPerPerson)
            .Select(f => new FaceSampleDto(f.PhotoId, urls.GetUrl(f.PhotoId, PhotoVariant.Thumb), urls.GetUrl(f.PhotoId, PhotoVariant.Web),
                f.Width, f.Height, new FaceBoxDto(f.BoxX, f.BoxY, f.BoxW, f.BoxH)))
            .ToList());

        var total = await db.Faces.CountAsync(f => f.EventId == eventId, ct);
        var running = ev.ClusteringStartedAt is { } started && started > time.GetUtcNow().AddMinutes(-30) &&
                      (ev.ClustersUpdatedAt is null || started > ev.ClustersUpdatedAt);
        return new PeopleDto(ev.ClustersUpdatedAt, running, total, faces.Count,
            clusters.Select(c => new PersonDto(c.Id, c.Number, c.Name, c.GuestId,
                c.GuestId is { } g ? guestNames.GetValueOrDefault(g) : null, c.FaceCount, c.PhotoCount,
                samples.GetValueOrDefault(c.Id) ?? [])).ToList());
    }

    public async Task StartAsync(Guid eventId, RunClusteringRequest req, CancellationToken ct)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct) ?? throw new NotFoundException("Event");
        ev.ClusteringStartedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        scheduler.EnqueueClustering(eventId, req.Eps ?? 0.5f, req.MinSamples ?? 3);
    }

    public async Task UpdateAsync(Guid clusterId, UpdatePersonRequest req, CancellationToken ct)
    {
        var cluster = await db.PersonClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct) ?? throw new NotFoundException("Person");
        cluster.Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim()[..Math.Min(100, req.Name.Trim().Length)];

        if (req.GuestId != cluster.GuestId)
        {
            if (cluster.GuestId is { } oldGuest) await UnlinkAsync(cluster, oldGuest, ct);
            if (req.GuestId is { } newGuest)
            {
                if (!await db.Guests.AnyAsync(g => g.Id == newGuest && g.EventId == cluster.EventId, ct))
                    throw new BadRequestException("That guest isn't part of this event.");
                await LinkAsync(cluster, newGuest, ct);
            }
            cluster.GuestId = req.GuestId;
        }
        await db.SaveChangesAsync(ct);
        if (req.GuestId is { } g && cluster.GuestId == g) await matcher.SearchForGuestAsync(g, ct); // expand with the new references
    }

    private async Task LinkAsync(PersonCluster cluster, Guid guestId, CancellationToken ct)
    {
        var faces = await db.Faces.Where(f => f.ClusterId == cluster.Id)
            .OrderByDescending(f => f.FaceSizePx).Select(f => new { f.Id, f.PhotoId, f.Embedding }).ToListAsync(ct);
        var now = time.GetUtcNow();

        // 1. Photos: best (= biggest) face per photo → Confirmed, unless the guest said "Not me".
        var existing = await db.GuestMatches.Where(m => m.GuestId == guestId).ToDictionaryAsync(m => m.PhotoId, ct);
        foreach (var face in faces.DistinctBy(f => f.PhotoId))
        {
            if (existing.TryGetValue(face.PhotoId, out var match))
            {
                if (match.Status != MatchStatus.Rejected) match.Status = MatchStatus.Confirmed;
            }
            else
            {
                db.GuestMatches.Add(new GuestMatch
                {
                    GuestId = guestId, PhotoId = face.PhotoId, FaceId = face.Id, Score = 1f, Status = MatchStatus.Confirmed, CreatedAt = now,
                });
            }
        }

        // 2. References: a few of the clearest faces, within the per-guest cap.
        var have = await db.GuestReferences.Where(r => r.GuestId == guestId).Select(r => r.FaceId).ToListAsync(ct);
        var room = MatchFeedbackService.MaxConfirmedReferences - have.Count(f => f != null);
        foreach (var face in faces.Where(f => !have.Contains(f.Id)).Take(Math.Min(ReferencesPerLink, Math.Max(0, room))))
        {
            db.GuestReferences.Add(new GuestReference
            {
                GuestId = guestId, FaceId = face.Id, Embedding = face.Embedding, Source = ReferenceSource.ConfirmedMatch, CreatedAt = now,
            });
        }
        db.AuditLogs.Add(new AuditLog { Actor = "admin", Action = "person_linked", EntityId = guestId.ToString(), Details = $"{faces.Count} faces" });
    }

    /// <summary>Stop searching with this person's faces. Photos already given stay (remove them in Match Review).</summary>
    private async Task UnlinkAsync(PersonCluster cluster, Guid guestId, CancellationToken ct)
    {
        var faceIds = await db.Faces.Where(f => f.ClusterId == cluster.Id).Select(f => (Guid?)f.Id).ToListAsync(ct);
        await db.GuestReferences.Where(r => r.GuestId == guestId && faceIds.Contains(r.FaceId)).ExecuteDeleteAsync(ct);
    }
}
