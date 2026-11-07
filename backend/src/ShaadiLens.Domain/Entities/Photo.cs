using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Domain.Entities;

public class Photo
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public Guid CollectionId { get; set; }

    public string OriginalFileName { get; set; } = "";
    /// <summary>SHA-256 (hex) of the uploaded bytes — the same file uploaded twice is skipped.</summary>
    public string ContentHash { get; set; } = "";

    public string StorageKeyOriginal { get; set; } = "";
    /// <summary>2048px long edge JPEG, EXIF-oriented, metadata stripped.</summary>
    public string? StorageKeyWeb { get; set; }
    /// <summary>400px long edge JPEG.</summary>
    public string? StorageKeyThumb { get; set; }

    /// <summary>Upright (EXIF-oriented) size of the original.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset? TakenAt { get; set; }
    public string? CameraModel { get; set; }

    public PhotoStatus Status { get; set; } = PhotoStatus.Uploaded;
    public int FaceCount { get; set; }
    public float? BlurScore { get; set; }
    public bool IsHighlight { get; set; }
    public bool IsHidden { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }

    public Collection? Collection { get; set; }
    public List<Face> Faces { get; set; } = [];
}
