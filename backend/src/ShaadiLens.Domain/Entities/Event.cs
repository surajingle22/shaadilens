namespace ShaadiLens.Domain.Entities;

/// <summary>One wedding (or any event). Everything else hangs off an Event.</summary>
public class Event
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    /// <summary>URL-friendly name, unique (e.g. "suraj-priya-wedding").</summary>
    public string Slug { get; set; } = "";
    public DateOnly? Date { get; set; }
    public string? Venue { get; set; }
    public string? CoverImageKey { get; set; }
    /// <summary>One of the event's photos used as the cover on the guest landing page.</summary>
    public Guid? CoverPhotoId { get; set; }
    public string ThemeColor { get; set; } = "#b0004a";
    public string? TitleFont { get; set; }
    public bool WatermarkEnabled { get; set; }
    public string? WatermarkText { get; set; }
    /// <summary>Nothing is visible to guests until the admin publishes.</summary>
    public bool IsPublished { get; set; }
    public bool FaceSearchEnabled { get; set; } = true;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Per-event face matching thresholds (stored in the Events table as owned columns).</summary>
    public FaceSettings FaceSettings { get; set; } = new();

    public List<Collection> Collections { get; set; } = [];
}

/// <summary>
/// Thresholds for face matching. Similarity is cosine similarity of two 512-d embeddings (0..1 in practice).
/// See docs/AI-PIPELINE.md for what the numbers mean.
/// </summary>
public class FaceSettings
{
    /// <summary>Score ≥ this → "Your photos" (Auto).</summary>
    public float MatchThreshold { get; set; } = 0.42f;
    /// <summary>Score ≥ this (but below MatchThreshold) → "Might be you?" (Probable).</summary>
    public float ProbableThreshold { get; set; } = 0.34f;
    /// <summary>Ignore faces smaller than this (pixels, measured on the 2048px web image).</summary>
    public int MinFaceSizePx { get; set; } = 40;
    /// <summary>Ignore detections the detector isn't confident about (statues, patterns on clothes).</summary>
    public float MinDetScore { get; set; } = 0.6f;
    /// <summary>Selfies turned further than this (degrees) are rejected: "Look straight at the camera".</summary>
    public float MaxSelfieYaw { get; set; } = 30f;
}
