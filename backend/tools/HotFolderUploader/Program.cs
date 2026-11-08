// ShaadiLens hot-folder uploader — a free replacement for "upload while you shoot" tools.
//
// Watches a folder and uploads every new photo into one collection of an event.
//
//   shaadilens-uploader --folder "D:\Camera" --event "suraj-priya" --collection Wedding
//                       [--api http://localhost:8080] [--key <UPLOADER_API_KEY>] [--once] [--parallel 2]
//
// --key can also come from the SHAADILENS_KEY environment variable, --api from SHAADILENS_API.
// Run from the repo:  dotnet run --project backend/tools/HotFolderUploader -- --folder ... (see docs/RUNBOOK.md)

using ShaadiLens.HotFolderUploader;

var opts = ParseArgs(args);
if (opts is null)
{
    Console.WriteLine("""
        Usage: shaadilens-uploader --folder <path> --event <id|slug|name> --collection <name>
                                   [--api http://localhost:8080] [--key <api key>] [--once] [--parallel 2] [--no-recursive]
        The API key is UPLOADER_API_KEY from .env (or set SHAADILENS_KEY).
        """);
    return 2;
}

if (!Directory.Exists(opts["folder"])) { Console.Error.WriteLine($"Folder not found: {opts["folder"]}"); return 2; }
var key = opts.GetValueOrDefault("key") ?? Environment.GetEnvironmentVariable("SHAADILENS_KEY");
if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("Missing --key (UPLOADER_API_KEY from .env)."); return 2; }
var api = opts.GetValueOrDefault("api") ?? Environment.GetEnvironmentVariable("SHAADILENS_API") ?? "http://localhost:8080";

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var http = new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
var client = new UploadClient(http, key);
try
{
    var (ev, col) = await client.ResolveAsync(opts["event"], opts["collection"], cts.Token);
    var folder = new HotFolder(client,
        new HotFolderOptions(Path.GetFullPath(opts["folder"]), ev.EventId, col.Id,
            Recursive: !opts.ContainsKey("no-recursive"), Parallel: int.TryParse(opts.GetValueOrDefault("parallel"), out var p) ? p : 2),
        line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"));

    Console.WriteLine($"Uploading \"{opts["folder"]}\" → {ev.Name} / {col.Name}. Ctrl+C to stop.");
    if (opts.ContainsKey("once")) await folder.RunOnceAsync(cts.Token);
    else await folder.RunAsync(cts.Token);
    Console.WriteLine($"Done: {folder.Uploaded} uploaded, {folder.Duplicates} already there, {folder.Failed} failed.");
    return folder.Failed > 0 ? 1 : 0;
}
catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized)
{
    Console.Error.WriteLine("The API key was refused. Check UPLOADER_API_KEY in .env (24+ characters) and restart the api.");
    return 1;
}
catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static Dictionary<string, string>? ParseArgs(string[] args)
{
    var flags = new HashSet<string> { "once", "no-recursive" };
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) return null;
        var name = args[i][2..];
        if (flags.Contains(name)) result[name] = "true";
        else if (i + 1 < args.Length) result[name] = args[++i];
        else return null;
    }
    return result.ContainsKey("folder") && result.ContainsKey("event") && result.ContainsKey("collection") ? result : null;
}
