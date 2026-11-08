using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Events;
using ShaadiLens.HotFolderUploader;
using ShaadiLens.Tests.Support;

namespace ShaadiLens.Tests.Pipeline;

/// <summary>Phase 2e: the hot-folder uploader against the real API, authenticated with UPLOADER_API_KEY.</summary>
[Collection(DbCollection.Name)]
public sealed class HotFolderUploaderTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(pg);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "shaadilens-hotfolder", Guid.NewGuid().ToString("N"));
    private HttpClient _admin = null!;
    private EventDto _event = null!;

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminClientAsync();
        _event = await TestApi.CreateEventAsync(_admin);
        Directory.CreateDirectory(Path.Combine(_folder, "DCIM"));
        HotFolder.StableFor = TimeSpan.FromMilliseconds(50);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private UploadClient Client(string key = ApiFactory.UploaderKey) => new(_factory.CreateClient(), key);

    private HotFolder Folder(Guid collectionId, string stateFile, List<string> log) =>
        new(Client(), new HotFolderOptions(_folder, _event.Id, collectionId, StateFile: stateFile), log.Add);

    [Fact]
    public async Task Uploads_new_photos_once_and_skips_other_files()
    {
        await File.WriteAllBytesAsync(Path.Combine(_folder, "IMG_0001.JPG"), TestImages.CameraJpeg(640, 480, 1, "a"));
        await File.WriteAllBytesAsync(Path.Combine(_folder, "DCIM", "IMG_0002.jpg"), TestImages.CameraJpeg(640, 480, 1, "b"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "notes.txt"), "not a photo");
        await File.WriteAllBytesAsync(Path.Combine(_folder, ".hidden.jpg"), [1, 2, 3]);

        var (ev, collection) = await Client().ResolveAsync(_event.Slug, "wedding", CancellationToken.None);
        ev.EventId.Should().Be(_event.Id);
        var state = Path.Combine(_folder, "..", Guid.NewGuid().ToString("N") + ".txt");
        var log = new List<string>();

        var first = Folder(collection.Id, state, log);
        await first.RunOnceAsync(CancellationToken.None);
        (first.Uploaded, first.Failed).Should().Be((2, 0), string.Join("\n", log));

        var photos = await _admin.GetFromJsonAsync<PagedResult<Application.Photos.PhotoDto>>($"/api/v1/events/{_event.Id}/photos", Json.Options);
        photos!.Items.Select(p => p.FileName).Should().BeEquivalentTo(["IMG_0001.JPG", "IMG_0002.jpg"]);
        photos.Items.Should().OnlyContain(p => p.CollectionId == collection.Id);
        _factory.Scheduler.Processing.Should().HaveCount(2, "the normal pipeline takes over");

        // Restart with the same state file: nothing to do.
        var again = Folder(collection.Id, state, log);
        await again.RunOnceAsync(CancellationToken.None);
        again.Uploaded.Should().Be(0);

        // Lost state file: the server recognises the files (SHA-256) — no doubles.
        var fresh = Folder(collection.Id, state + ".new", log);
        await fresh.RunOnceAsync(CancellationToken.None);
        (fresh.Uploaded, fresh.Duplicates).Should().Be((0, 2));
    }

    [Fact]
    public async Task The_api_key_only_opens_the_upload_endpoints()
    {
        var wrong = async () => await Client("wrong-key-wrong-key-wrong-key").GetTargetsAsync(CancellationToken.None);
        (await wrong.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.UploaderKey);
        (await http.GetAsync("/api/v1/uploader/targets")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetAsync("/api/v1/events")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "admin endpoints don't accept the key");
        (await http.GetAsync($"/api/v1/events/{_event.Id}/guests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var resolve = async () => await Client().ResolveAsync(_event.Slug, "Honeymoon", CancellationToken.None);
        (await resolve.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no collection*Haldi*");
    }
}
