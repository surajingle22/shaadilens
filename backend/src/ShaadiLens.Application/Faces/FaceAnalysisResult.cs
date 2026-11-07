namespace ShaadiLens.Application.Faces;

/// <summary>Result of analysing one image. All coordinates are in original image pixels.</summary>
public sealed record FaceAnalysisResult(
    int Width,
    int Height,
    IReadOnlyList<DetectedFace> Faces,
    double ElapsedMs);

/// <summary>One detected face.</summary>
/// <param name="BBox">Face rectangle in pixels.</param>
/// <param name="Kps">5 keypoints [x,y]: left eye, right eye, nose, left mouth corner, right mouth corner.</param>
/// <param name="DetScore">Detector confidence 0..1. Low values are often false positives (patterns, statues).</param>
/// <param name="Embedding">512 floats, L2-normalised (length 1). Cosine similarity = dot product.</param>
/// <param name="Pose">Head rotation in degrees. |Yaw| &gt; ~30 = side profile, less reliable embedding.</param>
/// <param name="FacePx">min(width, height) of the box in pixels. Tiny faces give poor embeddings.</param>
/// <param name="Blur">Laplacian variance of the face crop; higher = sharper.</param>
public sealed record DetectedFace(
    FaceBox BBox,
    float[][] Kps,
    float DetScore,
    float[] Embedding,
    FacePose Pose,
    float FacePx,
    float Blur);

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
