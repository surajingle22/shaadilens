using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Photos;
using ShaadiLens.Application.Uploads;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Infrastructure.Storage;
using ShaadiLens.Tests.Support;

namespace ShaadiLens.Tests.Pipeline;

[Collection(DbCollection.Name)]
public sealed class UploadPipelineTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Chunked_upload_then_pipeline_produces_upright_stripped_images_and_faces()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(client);
        var collection = ev.Collections[0];
        var bytes = TestImages.CameraJpeg(seed: Guid.NewGuid().ToString("N")[..6]);

        // ---- upload in 64 KB chunks, sending them out of order to prove order doesn't matter ----
        var photoId = await UploadAsync(client, ev.Id, collection.Id, "DSC_0001.JPG", bytes, reverseOrder: true);
        _factory.Scheduler.Processing.Should().Contain(photoId);

        // ---- job 1: prepare image ----
        _factory.Faces.NextFaces =
        [
            FakeFaceEngine.Face(axis: 1, sizePx: 150),             // kept
            FakeFaceEngine.Face(axis: 2, sizePx: 20),              // too small (< MinFaceSizePx 40)
            FakeFaceEngine.Face(axis: 3, sizePx: 90, detScore: 0.4f), // detector not confident enough
        ];
        await RunJobAsync(j => j.PrepareAsync(photoId, null, CancellationToken.None));

        var photo = await GetPhotoAsync(client, photoId);
        photo.Photo.Status.Should().Be(PhotoStatus.Resized);
        // Pixels were 3000x2000 with EXIF orientation 6 → upright it's a 2000x3000 portrait.
        photo.Photo.Width.Should().Be(2000);
        photo.Photo.Height.Should().Be(3000);
        photo.Photo.TakenAt.Should().Be(new DateTimeOffset(2026, 12, 12, 18, 45, 3, TimeSpan.Zero));
        photo.CameraModel.Should().Be("TestCam X100");

        var storage = _factory.Services.GetRequiredService<LocalDiskStorage>();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.Photos.SingleAsync(p => p.Id == photoId);

            var web = TestImages.Inspect(storage.GetPath(entity.StorageKeyWeb!));
            web.Should().Be((1365u, 2048u, false), "web is 2048px long edge, upright, metadata stripped (no GPS)");
            var thumb = TestImages.Inspect(storage.GetPath(entity.StorageKeyThumb!));
            thumb.Should().Be((267u, 400u, false));
            File.ReadAllBytes(storage.GetPath(entity.StorageKeyOriginal)).Should().Equal(bytes, "the original is kept byte-for-byte");
        }

        // ---- job 2: detect faces ----
        await RunJobAsync(j => j.DetectFacesAsync(photoId, null, CancellationToken.None));
        photo = await GetPhotoAsync(client, photoId);
        photo.Photo.Status.Should().Be(PhotoStatus.FacesDetected);
        photo.Photo.FaceCount.Should().Be(1, "tiny and low-confidence faces are filtered out");
        var face = photo.Faces.Single();
        face.X.Should().BeApproximately(100f / 2048, 1e-4f, "boxes are stored normalised to 0..1");

        // Re-running is idempotent: faces are replaced, not duplicated.
        await RunJobAsync(j => j.DetectFacesAsync(photoId, null, CancellationToken.None));
        (await GetPhotoAsync(client, photoId)).Faces.Should().HaveCount(1);

        // ---- stats ----
        var stats = await client.GetFromJsonAsync<EventStatsDto>($"/api/v1/events/{ev.Id}/stats", Json.Options);
        stats!.Photos.Should().Be(1);
        stats.Processed.Should().Be(1);
        stats.Faces.Should().Be(1);
    }

    [Fact]
    public async Task Same_file_twice_is_reported_as_duplicate()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(client);
        var bytes = TestImages.CameraJpeg(800, 600, seed: "dup");

        var first = await UploadAsync(client, ev.Id, ev.Collections[0].Id, "a.jpg", bytes);
        var init = await InitAsync(client, ev.Id, ev.Collections[1].Id, "copy-of-a.jpg", bytes.Length);
        await PutChunksAsync(client, init, bytes);
        var complete = await (await client.PostAsync($"/api/v1/uploads/{init.UploadId}/complete", null))
            .Content.ReadFromJsonAsync<CompleteUploadResponse>(Json.Options);

        complete!.Status.Should().Be("duplicate");
        complete.PhotoId.Should().Be(first);
    }

    [Fact]
    public async Task Upload_status_reports_received_chunks_for_resume_and_complete_rejects_missing()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(client);
        var bytes = TestImages.CameraJpeg(1600, 1200, seed: "resume");
        var init = await InitAsync(client, ev.Id, ev.Collections[0].Id, "big.jpg", bytes.Length);
        init.TotalChunks.Should().BeGreaterThan(2);

        await PutChunkAsync(client, init, bytes, 0);
        await PutChunkAsync(client, init, bytes, 2);

        var status = await client.GetFromJsonAsync<UploadStatusResponse>($"/api/v1/uploads/{init.UploadId}", Json.Options);
        status!.ReceivedChunks.Should().Equal(0, 2);

        var res = await client.PostAsync($"/api/v1/uploads/{init.UploadId}/complete", null);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Missing chunks: 1");
    }

    [Fact]
    public async Task Unsupported_file_type_and_oversized_files_are_rejected_at_init()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(client);
        var cid = ev.Collections[0].Id;

        (await client.PostAsJsonAsync($"/api/v1/events/{ev.Id}/uploads", new InitUploadRequest(cid, "clip.mov", 1000)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/v1/events/{ev.Id}/uploads", new InitUploadRequest(cid, "huge.jpg", 51L * 1024 * 1024)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Broken_image_is_marked_failed_without_retrying()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(client);
        var garbage = new byte[5000];
        Random.Shared.NextBytes(garbage);

        var photoId = await UploadAsync(client, ev.Id, ev.Collections[0].Id, "broken.jpg", garbage);
        await RunJobAsync(j => j.PrepareAsync(photoId, null, CancellationToken.None));

        var photo = await GetPhotoAsync(client, photoId);
        photo.Photo.Status.Should().Be(PhotoStatus.Failed);
        photo.Photo.Error.Should().StartWith("Unreadable image");
    }

    [Fact]
    public async Task Media_urls_are_signed_and_tampering_is_rejected()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(client);
        var photoId = await UploadAsync(client, ev.Id, ev.Collections[0].Id, "m.jpg", TestImages.CameraJpeg(900, 600, seed: "media"));
        await RunJobAsync(j => j.PrepareAsync(photoId, null, CancellationToken.None));

        var page = await client.GetFromJsonAsync<PagedResult<PhotoDto>>($"/api/v1/events/{ev.Id}/photos", Json.Options);
        var thumbUrl = page!.Items.Single().ThumbUrl!;

        var anonymous = _factory.CreateClient();
        var ok = await anonymous.GetAsync(thumbUrl);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        ok.Headers.CacheControl!.Private.Should().BeTrue();

        // Same signature, different photo variant → rejected.
        (await anonymous.GetAsync(thumbUrl.Replace("/thumb?", "/original?"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Changed signature → rejected.
        (await anonymous.GetAsync(thumbUrl[..^2] + "xx")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------- helpers ----------

    private async Task RunJobAsync(Func<PhotoJobs, Task> job)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await job(scope.ServiceProvider.GetRequiredService<PhotoJobs>());
    }

    private static async Task<EventDto> CreateEventAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/v1/events",
            new SaveEventRequest($"Pipeline {Guid.NewGuid():N}", null, null, null, null, false, null, true, null));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<EventDto>(Json.Options))!;
    }

    private static async Task<PhotoDetailDto> GetPhotoAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PhotoDetailDto>($"/api/v1/photos/{id}", Json.Options))!;

    private static async Task<Guid> UploadAsync(HttpClient client, Guid eventId, Guid collectionId, string name, byte[] bytes, bool reverseOrder = false)
    {
        var init = await InitAsync(client, eventId, collectionId, name, bytes.Length);
        await PutChunksAsync(client, init, bytes, reverseOrder);
        var res = await client.PostAsync($"/api/v1/uploads/{init.UploadId}/complete", null);
        res.EnsureSuccessStatusCode();
        var complete = await res.Content.ReadFromJsonAsync<CompleteUploadResponse>(Json.Options);
        complete!.Status.Should().Be("created");
        return complete.PhotoId;
    }

    private static async Task<InitUploadResponse> InitAsync(HttpClient client, Guid eventId, Guid collectionId, string name, long size)
    {
        var res = await client.PostAsJsonAsync($"/api/v1/events/{eventId}/uploads", new InitUploadRequest(collectionId, name, size));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<InitUploadResponse>(Json.Options))!;
    }

    private static async Task PutChunksAsync(HttpClient client, InitUploadResponse init, byte[] bytes, bool reverse = false)
    {
        var indexes = Enumerable.Range(0, init.TotalChunks);
        foreach (var i in reverse ? indexes.Reverse() : indexes) await PutChunkAsync(client, init, bytes, i);
    }

    private static async Task PutChunkAsync(HttpClient client, InitUploadResponse init, byte[] bytes, int index)
    {
        var start = index * init.ChunkSize;
        var content = new ByteArrayContent(bytes, start, Math.Min(init.ChunkSize, bytes.Length - start));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        (await client.PutAsync($"/api/v1/uploads/{init.UploadId}/chunks/{index}", content)).EnsureSuccessStatusCode();
    }
}
