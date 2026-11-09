using Microsoft.EntityFrameworkCore;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Entities;

namespace ShaadiLens.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    /// <summary>buffalo_l (ArcFace) embeddings have 512 dimensions.</summary>
    public const int EmbeddingDimensions = 512;

    public DbSet<Event> Events => Set<Event>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<Face> Faces => Set<Face>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<GuestReference> GuestReferences => Set<GuestReference>();
    public DbSet<GuestMatch> GuestMatches => Set<GuestMatch>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<Selection> Selections => Set<Selection>();
    public DbSet<SelectionSubmission> SelectionSubmissions => Set<SelectionSubmission>();
    public DbSet<PersonCluster> PersonClusters => Set<PersonCluster>();
    public DbSet<Reel> Reels => Set<Reel>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // pgvector adds the `vector` column type + distance operators.
        b.HasPostgresExtension("vector");

        b.Entity<Event>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Slug).HasMaxLength(80);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.ThemeColor).HasMaxLength(7);
            // FaceSettings lives in the Events table as FaceSettings_MatchThreshold, … columns.
            e.OwnsOne(x => x.FaceSettings);
            e.HasMany(x => x.Collections).WithOne(c => c.Event).HasForeignKey(c => c.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Collection>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => new { x.EventId, x.SortOrder });
        });

        b.Entity<Photo>(e =>
        {
            e.Property(x => x.OriginalFileName).HasMaxLength(255);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.HasIndex(x => new { x.EventId, x.Status });
            e.HasIndex(x => new { x.EventId, x.CollectionId });
            e.HasIndex(x => new { x.CollectionId, x.TakenAt }); // burst grouping: "photos taken around the same time"
            e.HasIndex(x => x.BurstGroupId);
            // Same file can't be uploaded twice into one event.
            e.HasIndex(x => new { x.EventId, x.ContentHash }).IsUnique();
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Collection).WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Faces).WithOne(f => f.Photo).HasForeignKey(f => f.PhotoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Face>(e =>
        {
            e.Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.Property(x => x.LandmarksJson).HasColumnType("jsonb");
            e.HasIndex(x => x.EventId);
            // HNSW = approximate nearest-neighbour graph index. Makes "the 2000 faces most similar to
            // this selfie" fast even with 50k+ faces. vector_cosine_ops = index for the <=> operator.
            e.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });

        b.Entity<Guest>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(200);
            e.HasIndex(x => x.EventId);
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ShareLink>().WithMany().HasForeignKey(x => x.ShareLinkId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.References).WithOne().HasForeignKey(r => r.GuestId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Matches).WithOne().HasForeignKey(m => m.GuestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GuestReference>(e =>
        {
            e.Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.Property(x => x.ImageKey).HasMaxLength(300);
            e.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });

        b.Entity<Reel>(e =>
        {
            e.Property(x => x.PlanJson).HasColumnType("jsonb");
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => new { x.GuestId, x.CreatedAt });
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Guest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PersonCluster>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.Number });
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Guest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.SetNull);
            // Faces point at their cluster; deleting clusters (re-run) just clears the pointer.
            e.HasMany<Face>().WithOne().HasForeignKey(f => f.ClusterId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Selection>(e =>
        {
            // One heart per photo per Selection link.
            e.HasIndex(x => new { x.ShareLinkId, x.PhotoId }).IsUnique();
            e.Property(x => x.SelectedBy).HasMaxLength(100);
            e.Property(x => x.Comment).HasMaxLength(500);
            e.HasOne<ShareLink>().WithMany().HasForeignKey(x => x.ShareLinkId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Photo).WithMany().HasForeignKey(x => x.PhotoId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SelectionSubmission>(e =>
        {
            e.HasIndex(x => x.ShareLinkId).IsUnique();
            e.Property(x => x.SubmittedBy).HasMaxLength(100);
            e.HasOne<ShareLink>().WithMany().HasForeignKey(x => x.ShareLinkId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GuestMatch>(e =>
        {
            // One row per (guest, photo): the best-matching face in that photo.
            e.HasIndex(x => new { x.GuestId, x.PhotoId }).IsUnique();
            e.HasIndex(x => new { x.GuestId, x.FaceId });
            e.HasIndex(x => x.PhotoId);
            e.HasOne(x => x.Photo).WithMany().HasForeignKey(x => x.PhotoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Face>().WithMany().HasForeignKey(x => x.FaceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShareLink>(e =>
        {
            e.Property(x => x.Token).HasMaxLength(64);
            e.HasIndex(x => x.Token).IsUnique();
            e.Property(x => x.Label).HasMaxLength(100);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(80);
            e.Property(x => x.Action).HasMaxLength(60);
            e.HasIndex(x => x.At);
        });

        b.Entity<UploadSession>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255);
            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
