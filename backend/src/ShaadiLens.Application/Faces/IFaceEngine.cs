namespace ShaadiLens.Application.Faces;

/// <summary>
/// Anything that can find faces in an image and turn each face into a 512-number
/// "embedding" vector. Today this is the Python face-service (InsightFace) over HTTP;
/// because the rest of the app only sees this interface, a pure-.NET ONNX engine
/// could replace it later without touching use cases.
/// </summary>
public interface IFaceEngine
{
    /// <summary>Detect all faces in an image and return their boxes + embeddings.</summary>
    Task<FaceAnalysisResult> AnalyzeAsync(Stream image, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Check a guest selfie: exactly one big, sharp, frontal face. On success returns its embedding;
    /// otherwise a code (no_face, multiple_faces, too_small, low_confidence, turned, blurry) and a friendly reason.
    /// </summary>
    Task<SelfieCheck> CheckSelfieAsync(Stream image, float maxYaw, CancellationToken ct = default);

    /// <summary>Model / runtime information (for diagnostics).</summary>
    Task<FaceEngineInfo> GetInfoAsync(CancellationToken ct = default);
}

public sealed record SelfieCheck(bool Ok, string Code, string? Reason, float[]? Embedding);
