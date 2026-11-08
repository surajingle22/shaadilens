using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ShaadiLens.HotFolderUploader;

public sealed record HotFolderOptions(
    string Folder, Guid EventId, Guid CollectionId, bool Recursive = true, int Parallel = 2, string? StateFile = null);

/// <summary>
/// Watches a folder (where the camera's Wi-Fi card, tethering software or a card reader drops files) and uploads
/// every new photo once it has finished writing.
///
///   FileSystemWatcher (instant) + a rescan every 30 s (watchers miss events on network drives / USB cards)
///        ↓
///   "is it finished?"  size unchanged for 2 s AND we can open it  → cameras write files in several steps
///        ↓
///   upload (2 at a time, 3 tries with back-off) → remember it in the state file so restarts don't re-upload
/// </summary>
public sealed class HotFolder(UploadClient client, HotFolderOptions options, Action<string> log)
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp"];
    public static TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(2);
    public static TimeSpan RescanEvery { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly UploadState _state = new(options.StateFile ?? DefaultStateFile(options.EventId));

    private int _uploaded, _duplicates, _failed;
    public int Uploaded => _uploaded;
    public int Duplicates => _duplicates;
    public int Failed => _failed; // counters are shared by the parallel workers → Interlocked

    public static bool IsPhoto(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !Path.GetFileName(path).StartsWith('.');

    public static string DefaultStateFile(Guid eventId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaadiLens", $"uploaded-{eventId}.txt");

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var watcher = new FileSystemWatcher(options.Folder)
        {
            IncludeSubdirectories = options.Recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) => Enqueue(e.FullPath);
        watcher.Renamed += (_, e) => Enqueue(e.FullPath);

        var workers = Enumerable.Range(0, Math.Max(1, options.Parallel)).Select(_ => WorkAsync(ct)).ToList();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Scan();
                await Task.Delay(RescanEvery, ct);
            }
        }
        catch (OperationCanceledException) { }
        _queue.Writer.TryComplete();
        await Task.WhenAll(workers).ContinueWith(_ => { }, CancellationToken.None);
    }

    /// <summary>One pass: queue every photo in the folder that isn't uploaded yet.</summary>
    public int Scan()
    {
        var found = 0;
        foreach (var path in Directory.EnumerateFiles(options.Folder, "*", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            if (Enqueue(path)) found++;
        return found;
    }

    /// <summary>Upload everything currently in the folder, then return (used by --once and by tests).</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        Scan();
        _queue.Writer.TryComplete();
        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Parallel)).Select(_ => WorkAsync(ct)));
    }

    private bool Enqueue(string path)
    {
        if (!IsPhoto(path) || _state.Contains(path) || !_queued.TryAdd(path, 0)) return false;
        return _queue.Writer.TryWrite(path);
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var path in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (!await WaitUntilCompleteAsync(path, ct)) continue;
                await UploadWithRetryAsync(path, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            finally { _queued.TryRemove(path, out _); }
        }
    }

    /// <summary>Cameras/Wi-Fi cards write in steps: wait until the size stops changing and the file can be opened.</summary>
    private static async Task<bool> WaitUntilCompleteAsync(string path, CancellationToken ct)
    {
        long last = -1;
        for (var attempt = 0; attempt < 150; attempt++) // up to ~5 minutes for very slow transfers
        {
            if (!File.Exists(path)) return false;
            var size = new FileInfo(path).Length;
            if (size > 0 && size == last && CanOpen(path)) return true;
            last = size;
            await Task.Delay(StableFor, ct);
        }
        return false;
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task UploadWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await client.UploadAsync(options.EventId, options.CollectionId, path, ct);
                _state.Add(path);
                if (result.Status == "duplicate") Interlocked.Increment(ref _duplicates); else Interlocked.Increment(ref _uploaded);
                log($"{(result.Status == "duplicate" ? "=" : "↑")} {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB, {sw.Elapsed.TotalSeconds:0.0} s){(result.Status == "duplicate" ? " already uploaded" : "")}");
                return;
            }
            catch (HttpRequestException ex) when (attempt < 3 && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
                log($"! {Path.GetFileName(path)}: {ex.Message} — retrying");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
            catch (HttpRequestException ex)
            {
                Interlocked.Increment(ref _failed);
                log($"✗ {Path.GetFileName(path)}: {ex.Message}");
                return;
            }
        }
    }
}

/// <summary>Files already uploaded (path|size|modified), one per line, so restarts don't upload again.</summary>
internal sealed class UploadState
{
    private readonly string _file;
    private readonly HashSet<string> _done;
    private readonly Lock _lock = new();

    public UploadState(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _done = File.Exists(file) ? new HashSet<string>(File.ReadAllLines(file), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    public bool Contains(string path)
    {
        if (!File.Exists(path)) return false;
        lock (_lock) return _done.Contains(Key(path));
    }

    public void Add(string path)
    {
        var key = Key(path);
        lock (_lock)
        {
            if (_done.Add(key)) File.AppendAllLines(_file, [key]);
        }
    }
}
