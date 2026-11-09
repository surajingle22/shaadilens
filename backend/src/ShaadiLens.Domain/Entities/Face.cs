using Pgvector;

namespace ShaadiLens.Domain.Entities;

/// <summary>One face found in a photo by the face-service.</summary>
public class Face
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PhotoId { get; set; }
    /// <summary>Copied from the photo so similarity search can filter by event without a join.</summary>
    public Guid EventId { get; set; }

    // Bounding box, normalised 0..1 relative to the upright image (works for any variant size).
    public float BoxX { get; set; }
    public float BoxY { get; set; }
    public float BoxW { get; set; }
    public float BoxH { get; set; }

    /// <summary>5 keypoints as JSON [[x,y],…], normalised 0..1.</summary>
    public string LandmarksJson { get; set; } = "[]";
    public float DetScore { get; set; }

    /// <summary>
    /// 512 numbers describing the face, L2-normalised (length 1). Stored as pgvector `vector(512)`.
    /// Two faces of the same person have a small cosine distance (`&lt;=&gt;` operator).
    /// </summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>Sharpness (Laplacian variance of the face crop). Higher = sharper.</summary>
    public float QualityScore { get; set; }
    public float FaceSizePx { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    /// <summary>Eye aspect ratio (lid opening ÷ eye width, both eyes averaged): ~0.3 open, &lt; 0.17 closed. Null = unknown.</summary>
    public float? EyeAspectRatio { get; set; }
    /// <summary>"Person N" group from clustering (PersonCluster), or null (not grouped / seen once).</summary>
    public Guid? ClusterId { get; set; }

    public Photo? Photo { get; set; }
}
