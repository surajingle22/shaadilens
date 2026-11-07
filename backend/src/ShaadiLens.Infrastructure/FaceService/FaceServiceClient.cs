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

    public async Task<FaceEngineInfo> GetInfoAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("v1/info", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<FaceEngineInfo>(Json, ct)
               ?? throw new FaceServiceException("face-service returned an empty body");
    }

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
