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

    /// <summary>Re-render web + thumb from the original (watermark changed). Faces stay.</summary>
    void EnqueueImageRebuild(Guid photoId);

    /// <summary>Compute only the culling fields (blur, eyes, near-duplicates) for an already-processed photo.</summary>
    void EnqueueQualityAnalysis(Guid photoId);

    /// <summary>Queue a fresh search for every guest of the event (e.g. after the admin changed thresholds).</summary>
    void EnqueueRematchEvent(Guid eventId);

    /// <summary>Queue "group faces into people" (DBSCAN) for the event.</summary>
    void EnqueueClustering(Guid eventId, float eps, int minSamples);
}

/// <summary>Creates short-lived signed URLs for photo files (see MediaController).</summary>
public interface IMediaUrlSigner
{
    string GetUrl(Guid photoId, Domain.Enums.PhotoVariant variant);
    bool Verify(Guid photoId, Domain.Enums.PhotoVariant variant, long expires, string signature, DateTimeOffset now);
}
