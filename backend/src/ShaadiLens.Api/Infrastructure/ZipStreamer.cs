using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Guests;

namespace ShaadiLens.Api.Infrastructure;

/// <summary>
/// Streams a ZIP straight into the HTTP response: photos are written one by one, nothing is built in memory
/// or on disk first — so a 3 GB "download all" uses a few KB of RAM.
/// </summary>
public static class ZipStreamer
{
    public static async Task WriteAsync(HttpContext http, IStorageService storage, IReadOnlyList<ZipEntrySource> entries,
        string downloadName, CancellationToken ct)
    {
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{downloadName}\"";
        // ZipArchive writes synchronously when it finishes an entry; allow that for this response only.
        http.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

        using var zip = new ZipArchive(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = UniqueName(used, $"{Safe(entry.CollectionName)}/{Path.ChangeExtension(Safe(entry.FileName), Path.GetExtension(entry.StorageKey))}");
            // JPEGs are already compressed — storing them as-is is fastest.
            var zipEntry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            await using var target = zipEntry.Open();
            await using var source = await storage.OpenReadAsync(entry.StorageKey, ct);
            await source.CopyToAsync(target, ct);
        }
    }

    private static string Safe(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars().Append('/').Append('\\').ToArray())).Trim();

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++)
            candidate = $"{Path.ChangeExtension(name, null)} ({i}){Path.GetExtension(name)}";
        return candidate;
    }
}
