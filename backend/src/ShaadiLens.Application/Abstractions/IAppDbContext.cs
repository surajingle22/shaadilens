using Microsoft.EntityFrameworkCore;
using ShaadiLens.Domain.Entities;

namespace ShaadiLens.Application.Abstractions;

/// <summary>
/// The database as seen by use cases. Implemented by the EF Core AppDbContext in Infrastructure.
/// (Pragmatic choice: use cases query with LINQ directly instead of a repository per entity.)
/// </summary>
public interface IAppDbContext
{
    DbSet<Event> Events { get; }
    DbSet<Collection> Collections { get; }
    DbSet<Photo> Photos { get; }
    DbSet<Face> Faces { get; }
    DbSet<Guest> Guests { get; }
    DbSet<GuestReference> GuestReferences { get; }
    DbSet<GuestMatch> GuestMatches { get; }
    DbSet<ShareLink> ShareLinks { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<UploadSession> UploadSessions { get; }
    DbSet<Selection> Selections { get; }
    DbSet<SelectionSubmission> SelectionSubmissions { get; }
    DbSet<PersonCluster> PersonClusters { get; }
    DbSet<Reel> Reels { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
