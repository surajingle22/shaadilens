namespace ShaadiLens.Domain.Entities;

/// <summary>Who did what, when: downloads, deletions, PIN failures. Never contains faces, phones or emails.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>"admin", "guest:{id}", "link:{id}".</summary>
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? Ip { get; set; }
}

/// <summary>A chunked upload in progress. Chunks live on disk; this row holds the metadata.</summary>
public class UploadSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public Guid CollectionId { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? PhotoId { get; set; }
}
