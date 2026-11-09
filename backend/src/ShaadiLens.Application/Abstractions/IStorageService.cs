namespace ShaadiLens.Application.Abstractions;

/// <summary>
/// Blob storage addressed by "keys" like "events/{eventId}/photos/{photoId}/web.jpg".
/// Default implementation: local disk under STORAGE_ROOT. Could be MinIO/S3 later.
/// </summary>
public interface IStorageService
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    /// <summary>Open a stream to write a new file (overwrites).</summary>
    Stream OpenWrite(string key);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<long?> GetSizeAsync(string key, CancellationToken ct = default);
    Task MoveAsync(string fromKey, string toKey, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Delete everything under a "folder" prefix (e.g. all files of a photo).</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct = default);
    /// <summary>File names (not full keys) directly under a prefix.</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}

/// <summary>Central place for storage key conventions.</summary>
public static class StorageKeys
{
    public static string PhotoFolder(Guid eventId, Guid photoId) => $"events/{eventId}/photos/{photoId}";
    public static string Original(Guid eventId, Guid photoId, string ext) => $"{PhotoFolder(eventId, photoId)}/original{ext}";
    public static string Web(Guid eventId, Guid photoId) => $"{PhotoFolder(eventId, photoId)}/web.jpg";
    public static string Thumb(Guid eventId, Guid photoId) => $"{PhotoFolder(eventId, photoId)}/thumb.jpg";
    public static string UploadFolder(Guid uploadId) => $"tmp/uploads/{uploadId}";
    public static string UploadChunk(Guid uploadId, int index) => $"{UploadFolder(uploadId)}/{index:D5}.part";
    public static string Selfie(Guid eventId, Guid guestId, Guid selfieId) => $"events/{eventId}/selfies/{guestId}/{selfieId}.jpg";
    public static string GuestFolder(Guid eventId, Guid guestId) => $"events/{eventId}/selfies/{guestId}";
    /// <summary>Reels live in the guest's own folder: deleting the guest or the expiry purge removes them too.</summary>
    public static string Reel(Guid eventId, Guid guestId, Guid reelId) => $"{GuestFolder(eventId, guestId)}/reel-{reelId}.mp4";
}
