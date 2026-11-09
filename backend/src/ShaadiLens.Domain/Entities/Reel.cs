namespace ShaadiLens.Domain.Entities;

public enum ReelStatus
{
    /// <summary>Waiting for the reel worker.</summary>
    Queued = 0,
    Rendering = 1,
    Ready = 2,
    Failed = 3,
    /// <summary>A newer reel of the same guest is ready; this one's file was deleted.</summary>
    Replaced = 4,
}

/// <summary>A short 9:16 highlight video made from one guest's photos (FFmpeg, background job on the "video" queue).</summary>
public class Reel
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public Guid GuestId { get; set; }
    public ReelStatus Status { get; set; } = ReelStatus.Queued;

    /// <summary>The chosen photos in playing order, with where the guest's face is (JSON, see ReelPlan).</summary>
    public string PlanJson { get; set; } = "[]";
    public int PhotoCount { get; set; }
    public float DurationSeconds { get; set; }

    /// <summary>MP4 inside the guest's data folder, so deleting the guest (or the expiry purge) removes it too.</summary>
    public string? StorageKey { get; set; }
    public long SizeBytes { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
