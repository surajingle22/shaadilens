using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;

namespace ShaadiLens.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Folder for all photos/selfies. In Docker: /data/photos (a named volume).</summary>
    public string Root { get; set; } = "data/photos";
}

/// <summary>Stores blobs as plain files: key "events/x/photos/y/web.jpg" → {Root}/events/x/photos/y/web.jpg.</summary>
public sealed class LocalDiskStorage : IStorageService
{
    private readonly string _root;

    public LocalDiskStorage(IOptions<StorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.Root);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Absolute path for a key. Rejects ".." so a key can never escape the storage root.</summary>
    public string GetPath(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
            throw new ArgumentException($"Invalid storage key: {key}");
        return full;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        await using var file = OpenWrite(key);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(GetPath(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Stream OpenWrite(string key)
    {
        var path = GetPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(GetPath(key)));

    public Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        var fi = new FileInfo(GetPath(key));
        return Task.FromResult(fi.Exists ? fi.Length : (long?)null);
    }

    public Task MoveAsync(string fromKey, string toKey, CancellationToken ct = default)
    {
        var to = GetPath(toKey);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(GetPath(fromKey), to, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        var path = GetPath(prefix);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var path = GetPath(prefix);
        IReadOnlyList<string> names = Directory.Exists(path)
            ? Directory.GetFiles(path).Select(p => Path.GetFileName(p)).ToList()
            : [];
        return Task.FromResult(names);
    }
}
