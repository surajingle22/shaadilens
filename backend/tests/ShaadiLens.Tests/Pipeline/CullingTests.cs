using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Faces;
using ShaadiLens.Application.Photos;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.TestApi;

namespace ShaadiLens.Tests.Pipeline;

/// <summary>Culling report: blurry / eyes closed / near-duplicate bursts.</summary>
public sealed class CullingRulesTests
{
    private static readonly CullingOptions O = new();

    private static Face F(float px, float? ear, float yaw = 0) => new() { FaceSizePx = px, EyeAspectRatio = ear, Yaw = yaw };

    [Fact]
    public void Closed_eyes_count_only_main_frontal_faces()
    {
        Culling.CountClosedEyes([F(300, 0.10f), F(280, 0.30f)], O).Should().Be(1);
        Culling.CountClosedEyes([F(300, 0.30f), F(100, 0.05f)], O).Should().Be(0, "a blinking guest far behind the couple doesn't count");
        Culling.CountClosedEyes([F(50, 0.05f)], O).Should().Be(0, "landmarks on tiny faces are unreliable");
        Culling.CountClosedEyes([F(300, 0.05f, yaw: 60)], O).Should().Be(0, "side profiles give nonsense eye ratios");
        Culling.CountClosedEyes([F(300, null)], O).Should().Be(0);
    }

    [Fact]
    public void Hash_parsing_and_distance()
    {
        Culling.ParseHash("ffffffffffffffff").Should().Be(-1L);
        Culling.ParseHash("nope").Should().BeNull();
        Culling.Distance(0, Culling.ParseHash("000000000000000f")!.Value).Should().Be(4);
        Culling.Distance(0, -1).Should().Be(64);
    }

    [Fact]
    public void Blurry_uses_the_sharpest_face_or_the_whole_photo_without_faces()
    {
        Culling.IsBlurry(new Photo { FaceCount = 2, BlurScore = 40, Sharpness = 500 }, O).Should().BeTrue();
        Culling.IsBlurry(new Photo { FaceCount = 2, BlurScore = 200, Sharpness = 10 }, O).Should().BeFalse("bokeh background, sharp faces");
        Culling.IsBlurry(new Photo { FaceCount = 0, Sharpness = 10 }, O).Should().BeTrue();
    }
}

[Collection(DbCollection.Name)]
public sealed class CullingTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg);
    public void Dispose() => _factory.Dispose();

    private const string H0 = "0000000000000000";

    private Task<Guid> PhotoAsync(HttpClient admin, Guid eventId, Guid collectionId, string? phash, double sharpness, params DetectedFace[] faces)
    {
        _factory.Faces.NextPhash = phash;
        _factory.Faces.NextSharpness = sharpness;
        return AddProcessedPhotoAsync(_factory, admin, eventId, collectionId, faces);
    }

    private static async Task<List<PhotoDto>> ListAsync(HttpClient admin, Guid eventId, string filter) =>
        [.. (await admin.GetFromJsonAsync<PagedResult<PhotoDto>>($"/api/v1/events/{eventId}/photos?filter={filter}", Json.Options))!.Items];

    private async Task<Photo> DbPhotoAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Photos.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task Bursts_are_grouped_the_best_is_kept_and_filters_and_summary_work()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(admin);
        var c = ev.Collections[0].Id;

        var open = FakeFaceEngine.Face(1, sizePx: 200) with { Ear = 0.30f, Blur = 300 };
        var closed = FakeFaceEngine.Face(1, sizePx: 200) with { Ear = 0.08f, Blur = 400 };
        var a = await PhotoAsync(admin, ev.Id, c, H0, 500, open);
        var b = await PhotoAsync(admin, ev.Id, c, "0000000000000007", 500, closed); // 3 bits away → same burst
        var other = await PhotoAsync(admin, ev.Id, c, "ffffffffffffffff", 500, open);  // a different scene
        var blurry = await PhotoAsync(admin, ev.Id, c, "00000000ffffffff", 500, FakeFaceEngine.Face(2) with { Blur = 20 });
        var decor = await PhotoAsync(admin, ev.Id, c, "ffffffff00000000", 10);         // no faces, soft overall

        var dups = await ListAsync(admin, ev.Id, "Duplicates");
        dups.Select(p => p.Id).Should().Equal([a, b], "keeper first");
        dups[0].IsBurstBest.Should().BeTrue("eyes open beats the sharper photo with closed eyes");
        dups[0].BurstGroupId.Should().NotBeNull().And.Be(dups[1].BurstGroupId);

        (await ListAsync(admin, ev.Id, "EyesClosed")).Select(p => p.Id).Should().Equal(b);
        (await ListAsync(admin, ev.Id, "Blurry")).Select(p => p.Id).Should().BeEquivalentTo([blurry, decor]);
        (await DbPhotoAsync(other)).BurstGroupId.Should().BeNull();

        var summary = await admin.GetFromJsonAsync<CullingSummaryDto>($"/api/v1/events/{ev.Id}/photos/culling", Json.Options);
        summary.Should().Be(new CullingSummaryDto(5, 0, 2, 1, 1, 1, 0, 0));

        // The admin prefers b after all.
        (await admin.PostAsync($"/api/v1/photos/{b}/keep-in-burst", null)).EnsureSuccessStatusCode();
        (await DbPhotoAsync(b)).IsBurstBest.Should().BeTrue();
        (await DbPhotoAsync(a)).IsBurstBest.Should().BeFalse();

        // "Hide the rest" hides a only.
        var hide = await (await admin.PostAsync($"/api/v1/events/{ev.Id}/photos/hide-burst-extras", null)).Content.ReadFromJsonAsync<Dictionary<string, int>>();
        hide!["hidden"].Should().Be(1);
        (await DbPhotoAsync(a)).IsHidden.Should().BeTrue();
        (await DbPhotoAsync(b)).IsHidden.Should().BeFalse();

        // Deleting b leaves a burst of one → dissolved.
        (await admin.DeleteAsync($"/api/v1/photos/{b}")).EnsureSuccessStatusCode();
        (await DbPhotoAsync(a)).BurstGroupId.Should().BeNull();
    }

    [Fact]
    public async Task Groups_are_transitive_and_need_the_same_moment()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(admin);
        var c = ev.Collections[0].Id;

        var x = await PhotoAsync(admin, ev.Id, c, H0, 300);
        var y = await PhotoAsync(admin, ev.Id, c, "000000000000ffff", 300); // 16 bits from x: not similar
        (await DbPhotoAsync(y)).BurstGroupId.Should().BeNull();
        var z = await PhotoAsync(admin, ev.Id, c, "00000000000000ff", 300); // 8 from both → bridges them
        var group = (await DbPhotoAsync(z)).BurstGroupId;
        group.Should().NotBeNull();
        (await DbPhotoAsync(x)).BurstGroupId.Should().Be(group);
        (await DbPhotoAsync(y)).BurstGroupId.Should().Be(group);

        // Same picture but taken 5 minutes later (e.g. the same pose repeated) is not a burst.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = new[] { x, y, z };
            await db.Photos.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.TakenAt, p => p.TakenAt!.Value.AddMinutes(-5)));
        }
        var later = await PhotoAsync(admin, ev.Id, c, H0, 300);
        (await DbPhotoAsync(later)).BurstGroupId.Should().BeNull();

        // Another collection never joins.
        var elsewhere = await PhotoAsync(admin, ev.Id, ev.Collections[1].Id, H0, 300);
        (await DbPhotoAsync(elsewhere)).BurstGroupId.Should().BeNull();
    }

    [Fact]
    public async Task Backfill_fills_culling_fields_without_touching_faces()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(admin);
        var face = FakeFaceEngine.Face(1, sizePx: 200);

        var id = await PhotoAsync(admin, ev.Id, ev.Collections[0].Id, null, 0, face); // processed by an "old" face-service
        Guid faceId;
        await using (var scope = _factory.Services.CreateAsyncScope())
            faceId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Faces.Where(f => f.PhotoId == id).Select(f => f.Id).SingleAsync();

        var queued = await (await admin.PostAsync($"/api/v1/events/{ev.Id}/photos/analyze-quality", null)).Content.ReadFromJsonAsync<Dictionary<string, int>>();
        queued!["queued"].Should().Be(1);
        _factory.Scheduler.Quality.Should().Contain(id);

        _factory.Faces.NextFaces = [face with { Ear = 0.05f }];
        _factory.Faces.NextPhash = H0;
        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PhotoJobs>().AnalyzeQualityAsync(id, CancellationToken.None);

        var photo = await DbPhotoAsync(id);
        photo.PerceptualHash.Should().Be(0);
        photo.ClosedEyesCount.Should().Be(1);
        await using (var scope = _factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Faces.Where(f => f.PhotoId == id).Select(f => f.Id).SingleAsync())
                .Should().Be(faceId, "faces (and their matches / People groups) are kept");
    }
}
