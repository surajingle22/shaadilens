using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Abstractions;

/// <summary>
/// Finds a guest's photos: compares their reference embeddings (selfies, confirmed faces) with every
/// face of the event and writes GuestMatch rows. Implemented with pgvector in Infrastructure.
/// </summary>
public interface IFaceMatcher
{
    /// <summary>Forward search: one guest's references × all faces of the event ("selfie search", "find more").</summary>
    Task<MatchSummary> SearchForGuestAsync(Guid guestId, CancellationToken ct = default);

    /// <summary>
    /// Reverse search: the faces of one (new) photo × the references of every guest of the event. This is how
    /// guests get photos uploaded after they registered, without doing anything.
    /// </summary>
    /// <param name="decisions">Confirmed/Rejected decisions to restore (the photo's faces were just re-detected).</param>
    /// <returns>Guests who got a new photo in "Your photos" (for notifications).</returns>
    Task<IReadOnlyList<Guid>> MatchPhotoAsync(Guid photoId, IReadOnlyDictionary<Guid, MatchStatus>? decisions = null, CancellationToken ct = default);

    /// <summary>Histogram of best (guest, photo) similarities, so the admin can preview a threshold before saving it.</summary>
    Task<ThresholdHistogram> HistogramAsync(Guid eventId, CancellationToken ct = default);
}

/// <param name="Auto">Photos at/above MatchThreshold ("Your photos").</param>
/// <param name="Probable">Photos between ProbableThreshold and MatchThreshold ("Might be you?").</param>
public sealed record MatchSummary(int Auto, int Probable, int FacesCompared, double ElapsedMs);

/// <summary>
/// Buckets[i] = number of (guest, photo) pairs whose best similarity is in [i/100, (i+1)/100), for i ≥ MinBucket.
/// Pairs the guest/admin already decided (Confirmed / Rejected) are counted separately: thresholds don't move them.
/// </summary>
public sealed record ThresholdHistogram(int MinBucket, IReadOnlyDictionary<int, int> Buckets, int Confirmed, int Rejected, int Guests, double ElapsedMs)
{
    /// <summary>Pairs with similarity ≥ threshold (not counting Confirmed/Rejected).</summary>
    public int AtOrAbove(float threshold)
    {
        var from = (int)MathF.Round(threshold * 100);
        return Buckets.Where(b => b.Key >= from).Sum(b => b.Value);
    }
}

/// <summary>Image helpers that need a real image library (Magick.NET in Infrastructure).</summary>
public interface IImageTools
{
    /// <summary>Selfie → upright sRGB JPEG, ≤1280px, all metadata stripped. Throws BadRequestException if unreadable.</summary>
    Task<byte[]> NormalizeSelfieAsync(Stream input, CancellationToken ct = default);
}

public sealed record PosterInfo(string Title, string? Subtitle, string ThemeColor, string Url, bool IsFullGallery);

/// <summary>QR code + printable poster for a share link.</summary>
public interface IShareArtwork
{
    byte[] QrPng(string url, int pixelsPerModule = 20);
    /// <param name="size">"a4" or "a5" (portrait, 150 dpi).</param>
    byte[] PosterPng(PosterInfo info, string size);
}
