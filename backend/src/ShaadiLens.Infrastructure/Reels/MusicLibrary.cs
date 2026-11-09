using Microsoft.Extensions.Options;
using ShaadiLens.Application.Reels;

namespace ShaadiLens.Infrastructure.Reels;

/// <summary>
/// Royalty-free background music for reels: every audio file in the music folder (./music next to
/// docker-compose.yml, mounted read-only). You choose and license the tracks; none ship with ShaadiLens.
/// </summary>
public sealed class MusicLibrary(IOptions<ReelOptions> options)
{
    private static readonly string[] Extensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac"];

    public string Folder => options.Value.MusicFolder;

    public IReadOnlyList<string> Tracks()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith('.'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A random track, or null (the reel is silent).</summary>
    public string? PickRandom()
    {
        var tracks = Tracks();
        return tracks.Count == 0 ? null : tracks[Random.Shared.Next(tracks.Count)];
    }
}
