using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ShaadiLens.Application.Faces;

namespace ShaadiLens.Infrastructure.FaceService;

/// <summary>
/// <see cref="IFaceEngine"/> implemented as an HTTP call to the Python face-service.
/// Registered as a "typed HttpClient": IHttpClientFactory owns the HttpClient lifetime
/// (connection pooling, DNS refresh), we just receive a configured instance.
/// </summary>
public sealed class FaceServiceClient(HttpClient http) : IFaceEngine
{
    // Python returns snake_case (det_score, face_px, elapsed_ms) -> map to PascalCase records.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<FaceAnalysisResult> AnalyzeAsync(Stream image, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);

        using var response = await http.PostAsync("v1/analyze", content, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<FaceAnalysisResult>(Json, ct)
               ?? throw new FaceServiceException("face-service returned an empty body");
    }

    public async Task<SelfieCheck> CheckSelfieAsync(Stream image, float maxYaw, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "selfie.jpg");
        content.Add(new StringContent(maxYaw.ToString(System.Globalization.CultureInfo.InvariantCulture)), "max_yaw");

        using var response = await http.PostAsync("v1/selfie", content, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<SelfieCheck>(Json, ct)
               ?? throw new FaceServiceException("face-service returned an empty body");
    }

    public async Task<FaceEngineInfo> GetInfoAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("v1/info", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<FaceEngineInfo>(Json, ct)
               ?? throw new FaceServiceException("face-service returned an empty body");
    }

    /// <summary>Sends the embeddings as raw little-endian float32 (N × 512) — ~30 MB for 15k faces instead of ~100 MB of JSON.</summary>
    public async Task<int[]> ClusterAsync(IReadOnlyList<float[]> embeddings, float eps, int minSamples, CancellationToken ct = default)
    {
        if (embeddings.Count == 0) return [];
        var dim = embeddings[0].Length;
        var bytes = new byte[embeddings.Count * dim * sizeof(float)];
        for (var i = 0; i < embeddings.Count; i++)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(embeddings[i].AsSpan()).CopyTo(bytes.AsSpan(i * dim * sizeof(float)));
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Big-endian CPUs aren't supported");

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var url = FormattableString.Invariant($"v1/cluster?dim={dim}&eps={eps}&min_samples={minSamples}");
        using var response = await http.PostAsync(url, content, ct);
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<ClusterResponse>(Json, ct)
                     ?? throw new FaceServiceException("face-service returned an empty body");
        if (result.Labels.Length != embeddings.Count) throw new FaceServiceException("face-service returned the wrong number of labels");
        return result.Labels;
    }

    private sealed record ClusterResponse(int[] Labels, int Clusters, int Noise, double ElapsedMs);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new FaceServiceException($"face-service returned {(int)response.StatusCode}: {body}", (int)response.StatusCode);
    }
}

public sealed class FaceServiceException(string message, int? statusCode = null) : Exception(message)
{
    /// <summary>HTTP status from face-service (400 = bad image, 413 = too large), if any.</summary>
    public int? StatusCode { get; } = statusCode;
}
