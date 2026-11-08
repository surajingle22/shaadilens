namespace ShaadiLens.Domain.Entities;

/// <summary>
/// A "heart" on a photo through a Selection link — the couple picking photos for the printed album.
/// One row per (link, photo); a comment can say e.g. "crop tighter" or "black &amp; white".
/// </summary>
public class Selection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ShareLinkId { get; set; }
    public Guid PhotoId { get; set; }
    /// <summary>Name of whoever tapped the heart (from registration), if known.</summary>
    public string? SelectedBy { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Photo? Photo { get; set; }
}

/// <summary>"Submit selection": locks the link's picks so the photographer can start editing.</summary>
public class SelectionSubmission
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ShareLinkId { get; set; }
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? SubmittedBy { get; set; }
    /// <summary>True = no more changes. The admin can unlock it again.</summary>
    public bool Locked { get; set; } = true;
}
