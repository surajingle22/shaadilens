namespace ShaadiLens.Application.Abstractions;

/// <summary>
/// Queues background processing for a photo. Implemented with Hangfire in Infrastructure, so use
/// cases don't know about Hangfire.
/// </summary>
public interface IPhotoPipelineScheduler
{
    /// <summary>Queue the full pipeline: prepare image (EXIF, orient, resize) → detect faces.</summary>
    void EnqueueProcessing(Guid photoId);

    /// <summary>Queue only face detection again (e.g. after face-service was down).</summary>
    void EnqueueFaceDetection(Guid photoId);
}

/// <summary>Creates short-lived signed URLs for photo files (see MediaController).</summary>
public interface IMediaUrlSigner
{
    string GetUrl(Guid photoId, Domain.Enums.PhotoVariant variant);
    bool Verify(Guid photoId, Domain.Enums.PhotoVariant variant, long expires, string signature, DateTimeOffset now);
}
