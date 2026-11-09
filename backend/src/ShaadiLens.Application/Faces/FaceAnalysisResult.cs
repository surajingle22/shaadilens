namespace ShaadiLens.Application.Faces;

/// <summary>Result of analysing one image. All coordinates are in original image pixels.</summary>
/// <param name="Sharpness">Laplacian variance of the whole photo (512px); for photos without faces.</param>
/// <param name="Phash">64-bit perceptual hash as 16 hex chars; near-identical photos differ in few bits.</param>
public sealed record FaceAnalysisResult(
    int Width,
    int Height,
    IReadOnlyList<DetectedFace> Faces,
    double ElapsedMs,
    double? Sharpness = null,
    string? Phash = null);

/// <summary>One detected face.</summary>
/// <param name="BBox">Face rectangle in pixels.</param>
/// <param name="Kps">5 keypoints [x,y]: left eye, right eye, nose, left mouth corner, right mouth corner.</param>
/// <param name="DetScore">Detector confidence 0..1. Low values are often false positives (patterns, statues).</param>
/// <param name="Embedding">512 floats, L2-normalised (length 1). Cosine similarity = dot product.</param>
/// <param name="Pose">Head rotation in degrees. |Yaw| &gt; ~30 = side profile, less reliable embedding.</param>
/// <param name="FacePx">min(width, height) of the box in pixels. Tiny faces give poor embeddings.</param>
/// <param name="Blur">Laplacian variance of the face crop; higher = sharper.</param>
/// <param name="Ear">Eye aspect ratio from the 68 landmarks (~0.3 open, &lt; 0.17 closed); null if unknown.</param>
public sealed record DetectedFace(
    // Explicit name: the snake_case policy would otherwise look for "b_box".
    [property: System.Text.Json.Serialization.JsonPropertyName("bbox")] FaceBox BBox,
    float[][] Kps,
    float DetScore,
    float[] Embedding,
    FacePose Pose,
    float FacePx,
    float Blur,
    float? Ear = null);

public sealed record FaceBox(float X, float Y, float W, float H);

public sealed record FacePose(float Yaw, float Pitch, float Roll);

public sealed record FaceEngineInfo(
    string Model,
    string Insightface,
    string Onnxruntime,
    IReadOnlyList<string> Providers,
    int DetSize,
    int MaxSide,
    int EmbeddingDim);
