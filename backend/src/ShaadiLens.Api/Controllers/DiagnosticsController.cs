using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Application.Faces;

namespace ShaadiLens.Api.Controllers;

/// <summary>
/// Development-only endpoints to prove the .NET -> face-service link end-to-end from Swagger.
/// Returns 404 outside Development. (Becomes admin-only tooling in Phase 1.)
/// </summary>
[ApiController]
[Route("api/v1/diagnostics")]
public sealed class DiagnosticsController(IFaceEngine faceEngine, IWebHostEnvironment env) : ControllerBase
{
    [HttpGet("face-engine")]
    public async Task<ActionResult<FaceEngineInfo>> FaceEngine(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        return await faceEngine.GetInfoAsync(ct);
    }

    /// <summary>Upload an image; returns detected faces WITHOUT the embeddings (they're 512 numbers each).</summary>
    [HttpPost("face-analyze")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<ActionResult<FaceAnalyzeSummary>> FaceAnalyze(IFormFile file, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();

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
