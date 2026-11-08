using Pgvector;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Domain.Entities;

/// <summary>Someone who opened a share link and (for Guest links) registered with a selfie.</summary>
public class Guest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public Guid ShareLinkId { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? SelfieKey { get; set; }
    /// <summary>When the guest ticked "use my selfie to find my photos". Required before any face data is stored.</summary>
    public DateTimeOffset? ConsentGivenAt { get; set; }
    public string? PushSubscriptionJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastNotifiedAt { get; set; }
    /// <summary>Guest tapped "Notify me" and agreed to emails (needs Email).</summary>
    public bool NotifyByEmail { get; set; }
    /// <summary>A debounced notification job is waiting (see NotifyJobs). Null = none.</summary>
    public DateTimeOffset? NotifyScheduledAt { get; set; }
    /// <summary>Last time we searched all event faces for this guest.</summary>
    public DateTimeOffset? LastSearchedAt { get; set; }
    public bool IsVip { get; set; }

    public List<GuestReference> References { get; set; } = [];
    public List<GuestMatch> Matches { get; set; } = [];
}

/// <summary>
/// A face embedding known to belong to the guest: their selfie(s), and later faces they confirmed.
/// Searching with several references ("find more") improves recall for side profiles, makeup, etc.
/// </summary>
public class GuestReference
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GuestId { get; set; }
    public Vector Embedding { get; set; } = null!;
    public ReferenceSource Source { get; set; }
    public float Weight { get; set; } = 1f;
    /// <summary>For ConfirmedMatch: the face the guest confirmed (so it is added once, and removed again on "Not me").</summary>
    public Guid? FaceId { get; set; }
    /// <summary>For Selfie: the stored selfie image (admin Match Review shows it).</summary>
    public string? ImageKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>"Guest X appears in photo Y (via face Z) with similarity S".</summary>
public class GuestMatch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid GuestId { get; set; }
    public Guid PhotoId { get; set; }
    public Guid FaceId { get; set; }
    public float Score { get; set; }
    public MatchStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Photo? Photo { get; set; }
}
