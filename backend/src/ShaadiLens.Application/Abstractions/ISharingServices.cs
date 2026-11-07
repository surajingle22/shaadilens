namespace ShaadiLens.Application.Abstractions;

/// <summary>
/// Finds a guest's photos: compares their reference embeddings (selfies, confirmed faces) with every
/// face of the event and writes GuestMatch rows. Implemented with pgvector in Infrastructure.
/// </summary>
public interface IFaceMatcher
{
    Task<MatchSummary> SearchForGuestAsync(Guid guestId, CancellationToken ct = default);
}

/// <param name="Auto">Photos at/above MatchThreshold ("Your photos").</param>
/// <param name="Probable">Photos between ProbableThreshold and MatchThreshold ("Might be you?").</param>
public sealed record MatchSummary(int Auto, int Probable, int FacesCompared, double ElapsedMs);

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
