using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShaadiLens.HotFolderUploader;

public sealed record UploadCollection(Guid Id, string Name);
public sealed record UploadTarget(Guid EventId, string Name, string Slug, IReadOnlyList<UploadCollection> Collections);
public sealed record UploadResult(string Status, Guid PhotoId);

/// <summary>
/// Talks to ShaadiLens with the uploader API key (header X-Api-Key) and uses the same chunked, resumable
/// upload protocol as the admin web uploader: init → PUT chunks → complete. The server de-duplicates by
/// SHA-256, so uploading the same file twice is harmless ("duplicate").
/// </summary>
public sealed class UploadClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public UploadClient(HttpClient http, string apiKey)
    {
        _http = http;
        _http.DefaultRequestHeaders.Remove("X-Api-Key");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public async Task<IReadOnlyList<UploadTarget>> GetTargetsAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<UploadTarget>>("api/v1/uploader/targets", Json, ct) ?? [];

    /// <summary>Finds the event (id, slug or part of the name) and the collection (name) to upload into.</summary>
    public async Task<(UploadTarget Event, UploadCollection Collection)> ResolveAsync(string eventQuery, string collectionName, CancellationToken ct)
    {
        var targets = await GetTargetsAsync(ct);
        var ev = targets.FirstOrDefault(t => t.EventId.ToString() == eventQuery || t.Slug.Equals(eventQuery, StringComparison.OrdinalIgnoreCase))
                 ?? targets.SingleOrDefaultIfOne(t => t.Name.Contains(eventQuery, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException(
                     $"No single event matches \"{eventQuery}\". Events: {string.Join(", ", targets.Select(t => $"{t.Name} ({t.Slug})"))}");
        var col = ev.Collections.FirstOrDefault(c => c.Name.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(
                      $"Event \"{ev.Name}\" has no collection \"{collectionName}\". Collections: {string.Join(", ", ev.Collections.Select(c => c.Name))}");
        return (ev, col);
    }

    public async Task<UploadResult> UploadAsync(Guid eventId, Guid collectionId, string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var initRes = await _http.PostAsJsonAsync($"api/v1/events/{eventId}/uploads",
            new { collectionId, fileName = info.Name, size = info.Length }, Json, ct);
        await EnsureAsync(initRes, ct);
        var init = (await initRes.Content.ReadFromJsonAsync<InitResponse>(Json, ct))!;

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var buffer = new byte[init.ChunkSize];
        for (var i = 0; i < init.TotalChunks; i++)
        {
            var read = await file.ReadAtLeastAsync(buffer, Math.Min(init.ChunkSize, (int)(info.Length - (long)i * init.ChunkSize)), true, ct);
            using var chunk = new ByteArrayContent(buffer, 0, read);
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await EnsureAsync(await _http.PutAsync($"api/v1/uploads/{init.UploadId}/chunks/{i}", chunk, ct), ct);
        }

        var done = await _http.PostAsync($"api/v1/uploads/{init.UploadId}/complete", null, ct);
        await EnsureAsync(done, ct);
        return (await done.Content.ReadFromJsonAsync<UploadResult>(Json, ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}: {body}", null, res.StatusCode);
    }

    private sealed record InitResponse(Guid UploadId, int ChunkSize, int TotalChunks);
}

internal static class EnumerableExtensions
{
    /// <summary>The only match, or null when there are none or several (ambiguous).</summary>
    public static T? SingleOrDefaultIfOne<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        var matches = source.Where(predicate).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
