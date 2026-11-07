using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Faces;

namespace ShaadiLens.Api.Controllers;

/// <summary>Admin tools to test the .NET → face-service link directly (e.g. from Swagger).</summary>
[ApiController]
[Route("api/v1/diagnostics")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class DiagnosticsController(IFaceEngine faceEngine) : ControllerBase
{
    [HttpGet("face-engine")]
    public Task<FaceEngineInfo> FaceEngine(CancellationToken ct) => faceEngine.GetInfoAsync(ct);

    /// <summary>Upload an image; returns detected faces WITHOUT the embeddings (they're 512 numbers each).</summary>
    [HttpPost("face-analyze")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<FaceAnalyzeSummary> FaceAnalyze(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var result = await faceEngine.AnalyzeAsync(stream, file.FileName, ct);

        return new FaceAnalyzeSummary(
            result.Width,
            result.Height,
            result.Faces.Count,
            result.ElapsedMs,
            result.Faces.Select(f => new FaceSummary(
                f.BBox,
                MathF.Round(f.DetScore, 3),
                MathF.Round(f.FacePx),
                MathF.Round(f.Pose.Yaw, 1),
                MathF.Round(f.Blur, 1),
                f.Embedding.Length,
                // Should be 1.0: embeddings are L2-normalised.
                MathF.Round(MathF.Sqrt(f.Embedding.Sum(v => v * v)), 4))).ToList());
    }
}

public sealed record FaceAnalyzeSummary(int Width, int Height, int FaceCount, double ElapsedMs, IReadOnlyList<FaceSummary> Faces);

public sealed record FaceSummary(FaceBox BBox, float DetScore, float FacePx, float Yaw, float Blur, int EmbeddingDim, float EmbeddingNorm);
