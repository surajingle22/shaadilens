using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Guests;
using ShaadiLens.Application.Sharing;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;

namespace ShaadiLens.Tests.Guests;

/// <summary>Phase 2c: VIP link, Selection link (limit, comments, submit/lock, exports), watermark, expiry + purge.</summary>
[Collection(DbCollection.Name)]
public sealed class CoupleFeaturesTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(pg);
    private HttpClient _admin = null!;
    private EventDto _event = null!;
    private Guid _a, _b, _c;

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminClientAsync();
        _event = await TestApi.CreateEventAsync(_admin);
        var haldi = _event.Collections[0].Id;
        _a = await TestApi.AddNamedPhotoAsync(_factory, _admin, _event.Id, haldi, "DSC_0001.JPG", FaceWith(UnitVector(1)));
        _b = await TestApi.AddNamedPhotoAsync(_factory, _admin, _event.Id, haldi, "DSC_0002.JPG");
        _c = await TestApi.AddNamedPhotoAsync(_factory, _admin, _event.Id, haldi, "DSC_0003.JPG");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<ShareLinkDto> Link(ShareLinkType type, int? limit = null, RegistrationFields fields = RegistrationFields.Name)
    {
        var res = await _admin.PostAsJsonAsync($"/api/v1/events/{_event.Id}/share-links",
            new SaveShareLinkRequest(type, null, true, fields, true, true, null, true, limit), Json.Options);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ShareLinkDto>(Json.Options))!;
    }

    [Fact]
    public async Task Vip_link_is_selfie_only_without_a_form()
    {
        var link = await Link(ShareLinkType.VipGuest, fields: RegistrationFields.Name | RegistrationFields.Phone);
        var info = await _factory.CreateClient().GetFromJsonAsync<PublicLinkDto>($"/api/v1/public/links/{link.Token}", Json.Options);
        info!.SelfieRequired.Should().BeTrue();
        info.RequireRegistration.Should().BeFalse("VIP links never ask for details");
        (await TestApi.RegisterAsync(_factory.CreateClient(), link.Token, name: null, withSelfie: false))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the selfie is still required");

        var vip = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), name: null!);
        var mine = await vip.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options);
        mine!.Items.Select(p => p.Id).Should().Equal([_a]);
        (await vip.GetAsync("/api/v1/public/gallery")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "VIPs see their own photos, not everything");

        await using var scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Guests.SingleAsync(g => g.ShareLinkId == link.Id)).IsVip.Should().BeTrue();
    }

    [Fact]
    public async Task Selection_link_enforces_the_limit_then_locks_on_submit()
    {
        var link = await Link(ShareLinkType.Selection, limit: 2);
        var couple = await TestApi.GuestClientAsync(_factory, link.Token, selfieEmbedding: null, name: "Priya & Rahul");
        (await couple.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/gallery", Json.Options))!.Total.Should().Be(3);

        (await couple.PostAsync($"/api/v1/public/selections/{_a}", null)).EnsureSuccessStatusCode();
        var state = await (await couple.PostAsync($"/api/v1/public/selections/{_b}", null)).Content.ReadFromJsonAsync<SelectionStateDto>(Json.Options);
        (state!.Count, state.Limit).Should().Be((2, 2));
        (await couple.PostAsync($"/api/v1/public/selections/{_c}", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "2 / 2 reached");
        (await couple.PostAsync($"/api/v1/public/selections/{_a}", null)).StatusCode.Should().Be(HttpStatusCode.OK, "re-hearting is harmless");

        (await couple.PutAsJsonAsync($"/api/v1/public/selections/{_c}/comment", new CommentRequest("x"))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "comment needs a heart first");
        (await couple.PutAsJsonAsync($"/api/v1/public/selections/{_a}/comment", new CommentRequest("=cmd|' crop tighter"))).EnsureSuccessStatusCode();
        (await couple.DeleteAsync($"/api/v1/public/selections/{_b}")).EnsureSuccessStatusCode();
        (await couple.PostAsync($"/api/v1/public/selections/{_c}", null)).EnsureSuccessStatusCode();

        var submitted = await (await couple.PostAsync("/api/v1/public/selections/submit", null)).Content.ReadFromJsonAsync<SelectionStateDto>(Json.Options);
        submitted!.Locked.Should().BeTrue();
        (await couple.DeleteAsync($"/api/v1/public/selections/{_a}")).StatusCode.Should().Be(HttpStatusCode.Conflict, "locked after submit");

        // Admin sees the picks and exports them.
        var picks = (await _admin.GetFromJsonAsync<List<SelectionLinkDto>>($"/api/v1/events/{_event.Id}/selections", Json.Options))!.Single();
        picks.Locked.Should().BeTrue();
        picks.SubmittedBy.Should().Be("Priya & Rahul");
        picks.Items.Select(i => i.FileName).Should().BeEquivalentTo(["DSC_0001.JPG", "DSC_0003.JPG"]);
        (await _admin.GetStringAsync($"/api/v1/events/{_event.Id}/selections/export?format=names")).Should().Be("DSC_0001, DSC_0003");
        var csv = await _admin.GetStringAsync($"/api/v1/events/{_event.Id}/selections/export?format=csv");
        csv.Should().Contain("\"'=cmd|' crop tighter\"", "formula-looking comments are defused for Excel");
        using (var zip = new ZipArchive(await _admin.GetStreamAsync($"/api/v1/events/{_event.Id}/selections/export?format=zip")))
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["Haldi/DSC_0001.jpg", "Haldi/DSC_0003.jpg"]);

        (await _admin.PostAsync($"/api/v1/share-links/{link.Id}/selection/unlock", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await couple.DeleteAsync($"/api/v1/public/selections/{_a}")).StatusCode.Should().Be(HttpStatusCode.OK, "unlocked again");
    }

    [Fact]
    public async Task Only_selection_links_can_pick_photos()
    {
        var full = await Link(ShareLinkType.Full);
        var viewer = await TestApi.GuestClientAsync(_factory, full.Token, selfieEmbedding: null, name: "Uncle");
        (await viewer.PostAsync($"/api/v1/public/selections/{_a}", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Watermark_goes_on_the_web_image_only_and_settings_are_validated()
    {
        using var plain = new MagickImage(MagickColors.SteelBlue, 1200, 800);
        using var marked = (MagickImage)plain.Clone();
        ImageProcessor.DrawWatermark(marked, "Studio Lens");
        using (var corner = (MagickImage)marked.Clone()) using (var plainCorner = (MagickImage)plain.Clone())
        {
            corner.Crop(new MagickGeometry(800, 650, 400, 150));
            plainCorner.Crop(new MagickGeometry(800, 650, 400, 150));
            corner.Compare(plainCorner, ErrorMetric.RootMeanSquared).Should().BeGreaterThan(0.01, "text is drawn bottom-right");
        }
        using (var topLeft = (MagickImage)marked.Clone())
        {
            topLeft.Crop(new MagickGeometry(0, 0, 400, 300));
            using var plainTopLeft = (MagickImage)plain.Clone();
            plainTopLeft.Crop(new MagickGeometry(0, 0, 400, 300));
            topLeft.Compare(plainTopLeft, ErrorMetric.RootMeanSquared).Should().Be(0);
        }

        var noText = new SaveEventRequest(_event.Name, null, null, null, null, WatermarkEnabled: true, WatermarkText: null, true, null);
        (await _admin.PutAsJsonAsync($"/api/v1/events/{_event.Id}", noText)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var badFont = noText with { WatermarkText = "Studio Lens", TitleFont = "Comic Sans\"; }" };
        (await _admin.PutAsJsonAsync($"/api/v1/events/{_event.Id}", badFont)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PutAsJsonAsync($"/api/v1/events/{_event.Id}", badFont with { TitleFont = "Great Vibes" })).EnsureSuccessStatusCode();

        var rebuild = await (await _admin.PostAsync($"/api/v1/events/{_event.Id}/photos/rebuild-images", null)).Content.ReadFromJsonAsync<Dictionary<string, int>>(Json.Options);
        rebuild!["queued"].Should().Be(3);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var webKey = (await db.Photos.SingleAsync(p => p.Id == _b)).StorageKeyWeb!;
        var before = await File.ReadAllBytesAsync(Path.Combine(_factory.StorageRoot, webKey));
        await scope.ServiceProvider.GetRequiredService<PhotoJobs>().RebuildImagesAsync(_b, CancellationToken.None);
        (await File.ReadAllBytesAsync(Path.Combine(_factory.StorageRoot, webKey))).Should().NotEqual(before, "web image now carries the watermark");
    }

    [Fact]
    public async Task Expired_events_close_and_their_face_data_is_purged()
    {
        var link = await Link(ShareLinkType.Guest);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");
        var picks = await Link(ShareLinkType.Selection);
        var couple = await TestApi.GuestClientAsync(_factory, picks.Token, selfieEmbedding: null, name: "Couple");
        (await couple.PostAsync($"/api/v1/public/selections/{_a}", null)).EnsureSuccessStatusCode();

        string selfieKey;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            selfieKey = (await db.Guests.SingleAsync(g => g.ShareLinkId == link.Id)).SelfieKey!;
            var ev = await db.Events.SingleAsync(e => e.Id == _event.Id);
            ev.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        (await asha.GetAsync("/api/v1/public/me/photos")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "expired → links close at once");
        (await _factory.CreateClient().GetAsync($"/api/v1/public/links/{link.Token}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PurgeJobs>().PurgeExpiredAsync(CancellationToken.None);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Guests.CountAsync(g => g.EventId == _event.Id)).Should().Be(0);
            (await db.Faces.CountAsync(f => f.EventId == _event.Id)).Should().Be(0, "embeddings are biometric data");
            (await db.Photos.CountAsync(p => p.EventId == _event.Id)).Should().Be(3, "the gallery itself stays");
            (await db.Selections.CountAsync(s => s.ShareLinkId == picks.Id)).Should().Be(1, "the couple's picks stay");
            var ev = await db.Events.SingleAsync(e => e.Id == _event.Id);
            ev.PurgedAt.Should().NotBeNull();
            ev.FaceSearchEnabled.Should().BeFalse();
        }
        File.Exists(Path.Combine(_factory.StorageRoot, selfieKey)).Should().BeFalse();
    }
}
