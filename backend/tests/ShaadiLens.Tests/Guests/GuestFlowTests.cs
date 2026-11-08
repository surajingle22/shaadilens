using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Api.Controllers;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Guests;
using ShaadiLens.Application.Sharing;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;

namespace ShaadiLens.Tests.Guests;

/// <summary>
/// End-to-end guest scenarios through the real API + real Postgres/pgvector, with a fake face engine so
/// we control exactly how similar each face is to each selfie.
///
/// Embedding "people": Asha = axis 1, Bunty = axis 2, Chacha = axis 3. Default thresholds: Auto ≥ 0.42, Probable ≥ 0.34.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class GuestFlowTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(pg);
    private HttpClient _admin = null!;
    private EventDto _event = null!;
    private Guid _p1Solo, _p2Group, _p3Blurry, _p4Other, _p5Hidden;

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminClientAsync();
        _event = await TestApi.CreateEventAsync(_admin);
        var haldi = _event.Collections[0].Id;
        var wedding = _event.Collections[3].Id;

        _p1Solo = await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, haldi, FaceWith(UnitVector(1)));
        _p2Group = await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, wedding,
            FaceWith(WithSimilarity(1, 0.50f, 9), x: 100), FaceWith(UnitVector(2), x: 600));        // Asha (0.50) + Bunty
        _p3Blurry = await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, wedding, FaceWith(WithSimilarity(1, 0.38f, 9))); // "might be Asha"
        _p4Other = await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, wedding, FaceWith(UnitVector(3)));                 // Chacha only
        _p5Hidden = await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, haldi, FaceWith(UnitVector(1)));
        (await _admin.PatchAsJsonAsync($"/api/v1/photos/{_p5Hidden}", new { isHidden = true })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Selfie_search_splits_auto_and_probable_and_skips_hidden_photos()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");

        var mine = await asha.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options);
        mine!.Items.Select(p => p.Id).Should().BeEquivalentTo([_p1Solo, _p2Group], "solo (1.0) and group (0.50) are ≥ 0.42");

        var probable = await asha.GetFromJsonAsync<List<GuestPhotoDto>>("/api/v1/public/me/probable", Json.Options);
        probable!.Select(p => p.Id).Should().Equal([_p3Blurry], "0.38 is between 0.34 and 0.42");

        var me = await asha.GetFromJsonAsync<GuestSessionDto>("/api/v1/public/me", Json.Options);
        me!.MatchCount.Should().Be(2);
        me.ProbableCount.Should().Be(1);
        me.GuestName.Should().Be("Asha");

        var tabs = await asha.GetFromJsonAsync<List<GuestCollectionDto>>("/api/v1/public/collections", Json.Options);
        tabs!.Select(t => (t.Name, t.PhotoCount)).Should().Equal(("Haldi", 1), ("Wedding", 1));
    }

    [Fact]
    public async Task Group_photo_appears_for_every_guest_in_it()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var bunty = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(2), "Bunty");

        var mine = await bunty.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options);
        mine!.Items.Select(p => p.Id).Should().Equal([_p2Group]);
    }

    [Fact]
    public async Task Guest_link_cannot_reach_photos_they_are_not_in()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");

        (await asha.PostAsync($"/api/v1/public/photos/{_p4Other}/download", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await asha.PostAsync($"/api/v1/public/photos/{_p5Hidden}/download", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await asha.GetAsync("/api/v1/public/gallery")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await asha.GetAsync($"/api/v1/photos/{_p4Other}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "guest token is not an admin token");

        // A photo she IS in: signed original URL that works without any login.
        var url = (await (await asha.PostAsync($"/api/v1/public/photos/{_p1Solo}/download", null)).Content.ReadFromJsonAsync<UrlResponse>(Json.Options))!.Url;
        var file = await _factory.CreateClient().GetAsync(url);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentDisposition!.FileName.Should().Contain("p.jpg");
    }

    [Fact]
    public async Task Rejected_matches_never_come_back_and_threshold_changes_apply_on_refresh()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");

        // Asha says "Not me" on the solo photo (Phase 2 UI; set directly here).
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var match = await db.GuestMatches.SingleAsync(m =>
                m.PhotoId == _p1Solo && db.Guests.Any(g => g.Id == m.GuestId && g.ShareLinkId == link.Id));
            match.Status = MatchStatus.Rejected;
            await db.SaveChangesAsync();
        }

        // Admin raises the Auto threshold above the group photo's 0.50.
        var settings = _event.FaceSettings with { MatchThreshold = 0.55f };
        (await _admin.PutAsJsonAsync($"/api/v1/events/{_event.Id}/face-settings", settings)).EnsureSuccessStatusCode();

        (await asha.PostAsync("/api/v1/public/me/refresh", null)).EnsureSuccessStatusCode();

        var mine = await asha.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options);
        mine!.Items.Should().BeEmpty("solo is Rejected, group (0.50) is now below 0.55");
        var probable = await asha.GetFromJsonAsync<List<GuestPhotoDto>>("/api/v1/public/me/probable", Json.Options);
        probable!.Select(p => p.Id).Should().BeEquivalentTo([_p2Group, _p3Blurry]);
        probable!.Select(p => p.Id).Should().NotContain(_p1Solo, "Rejected is never shown again, even though it's a 1.0 match");

        (await _admin.PutAsJsonAsync($"/api/v1/events/{_event.Id}/face-settings", _event.FaceSettings)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Full_link_shows_all_visible_photos_without_registration()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Full, requireRegistration: false);
        var viewer = await TestApi.GuestClientAsync(_factory, link.Token, selfieEmbedding: null);

        var gallery = await viewer.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/gallery", Json.Options);
        gallery!.Items.Select(p => p.Id).Should().BeEquivalentTo([_p1Solo, _p2Group, _p3Blurry, _p4Other]);
        (await viewer.GetAsync("/api/v1/public/me/photos")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "no selfie → no 'your photos'");
    }

    [Fact]
    public async Task Download_all_streams_a_zip_of_my_photos()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");

        var url = (await (await asha.PostAsJsonAsync("/api/v1/public/me/download-all", new { })).Content.ReadFromJsonAsync<UrlResponse>(Json.Options))!.Url;
        var res = await _factory.CreateClient().GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync());
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["Haldi/p.jpg", "Wedding/p.jpg"]);

        (await _factory.CreateClient().GetAsync(url + "x")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "tampered ticket");
    }

    [Fact]
    public async Task Downloads_can_be_disabled_per_link()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest, downloads: false);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");
        (await asha.PostAsync($"/api/v1/public/photos/{_p1Solo}/download", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await asha.PostAsJsonAsync("/api/v1/public/me/download-all", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bad_selfie_returns_422_with_reason_and_creates_no_guest()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        _factory.Faces.NextSelfie = new(false, "too_small", "Move closer — your face should fill most of the oval.", null);

        var res = await TestApi.RegisterAsync(_factory.CreateClient(), link.Token, "Dolly");
        res.StatusCode.Should().Be((HttpStatusCode)422);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"too_small\"").And.Contain("Move closer");

        var guests = await _admin.GetFromJsonAsync<List<GuestAdminDto>>($"/api/v1/events/{_event.Id}/guests", Json.Options);
        guests.Should().NotContain(g => g.Name == "Dolly");
    }

    [Fact]
    public async Task Consent_and_required_fields_are_enforced()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest, fields: RegistrationFields.Name | RegistrationFields.Phone);
        _factory.Faces.NextSelfie = GoodSelfie(UnitVector(1));
        var client = _factory.CreateClient();

        (await TestApi.RegisterAsync(client, link.Token, "Asha", consent: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noPhone = await TestApi.RegisterAsync(client, link.Token, "Asha");
        noPhone.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noPhone.Content.ReadAsStringAsync()).Should().Contain("phone");
        (await TestApi.RegisterAsync(client, link.Token, "Asha", withSelfie: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pin_protects_the_link_and_five_wrong_pins_block_further_tries()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var pin = (await (await _admin.PostAsJsonAsync($"/api/v1/share-links/{link.Id}/pin", new PinRequest("generate", null)))
            .Content.ReadFromJsonAsync<PinResponse>(Json.Options))!.Pin!;
        pin.Should().MatchRegex("^[0-9]{6}$");
        var client = _factory.CreateClient();
        _factory.Faces.NextSelfie = GoodSelfie(UnitVector(1));

        (await client.GetFromJsonAsync<PublicLinkDto>($"/api/v1/public/links/{link.Token}", Json.Options))!.RequiresPin.Should().BeTrue();
        (await TestApi.RegisterAsync(client, link.Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "PIN not entered");

        // Correct PIN → ticket → register works.
        var ok = await client.PostAsJsonAsync($"/api/v1/public/links/{link.Token}/verify-pin", new VerifyPinRequest(pin));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var ticket = (await ok.Content.ReadFromJsonAsync<PinTicketResponse>(Json.Options))!.PinTicket;
        (await TestApi.RegisterAsync(client, link.Token, pinTicket: ticket)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Five wrong PINs, then even the right one is refused for 15 minutes.
        var wrong = pin == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
            (await client.PostAsJsonAsync($"/api/v1/public/links/{link.Token}/verify-pin", new VerifyPinRequest(wrong)))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var blocked = await client.PostAsJsonAsync($"/api/v1/public/links/{link.Token}/verify-pin", new VerifyPinRequest(pin));
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.Should().NotBeNull();

        // A ticket for one link doesn't open another link.
        var other = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        await _admin.PostAsJsonAsync($"/api/v1/share-links/{other.Id}/pin", new PinRequest("set", "123456"));
        (await TestApi.RegisterAsync(client, other.Token, pinTicket: ticket)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Closed_or_unpublished_galleries_refuse_guests()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var asha = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");

        var update = new SaveShareLinkRequest(ShareLinkType.Guest, null, true, RegistrationFields.Name, true, true, null, IsActive: false);
        (await _admin.PutAsJsonAsync($"/api/v1/share-links/{link.Id}", update, Json.Options)).EnsureSuccessStatusCode();

        (await asha.GetAsync("/api/v1/public/me/photos")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "existing sessions stop working too");
        (await _factory.CreateClient().GetAsync($"/api/v1/public/links/{link.Token}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().GetAsync("/api/v1/public/links/not-a-real-token")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var draft = await TestApi.CreateEventAsync(_admin, publish: false);
        var draftLink = await TestApi.CreateLinkAsync(_admin, draft.Id, ShareLinkType.Full);
        (await _factory.CreateClient().GetAsync($"/api/v1/public/links/{draftLink.Token}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_my_data_removes_guest_and_selfie()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var eve = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Eve");

        string selfieKey;
        await using (var scope = _factory.Services.CreateAsyncScope())
            selfieKey = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Guests.SingleAsync(g => g.Name == "Eve" && g.ShareLinkId == link.Id)).SelfieKey!;
        File.Exists(Path.Combine(_factory.StorageRoot, selfieKey)).Should().BeTrue();

        (await eve.DeleteAsync("/api/v1/public/me")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eve.GetAsync("/api/v1/public/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        File.Exists(Path.Combine(_factory.StorageRoot, selfieKey)).Should().BeFalse();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Guests.AnyAsync(g => g.Name == "Eve" && g.ShareLinkId == link.Id)).Should().BeFalse();
            (await db.GuestReferences.CountAsync(r => !db.Guests.Any(g => g.Id == r.GuestId))).Should().Be(0, "embeddings are deleted with the guest");
        }
    }

    [Fact]
    public async Task Qr_and_posters_render()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        using var qr = new MagickImage(await _admin.GetByteArrayAsync($"/api/v1/share-links/{link.Id}/qr.png"));
        qr.Format.Should().Be(MagickFormat.Png);

        using var a4 = new MagickImage(await _admin.GetByteArrayAsync($"/api/v1/share-links/{link.Id}/poster.png"));
        (a4.Width, a4.Height).Should().Be((1240u, 1754u));
        using var a5 = new MagickImage(await _admin.GetByteArrayAsync($"/api/v1/share-links/{link.Id}/poster.png?size=a5"));
        (a5.Width, a5.Height).Should().Be((874u, 1240u));
    }
}
