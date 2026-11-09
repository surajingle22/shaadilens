using System.Net.Http.Json;
using FluentAssertions;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Faces;
using ShaadiLens.Application.Guests;
using ShaadiLens.Application.Photos;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;
using static ShaadiLens.Tests.Support.TestApi;

namespace ShaadiLens.Tests.Guests;

/// <summary>Highlights strip ("stories") for guests + "Suggest highlights" for the admin.</summary>
[Collection(DbCollection.Name)]
public sealed class HighlightTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg);
    public void Dispose() => _factory.Dispose();

    private Task<Guid> PhotoAsync(HttpClient admin, Guid eventId, Guid collectionId, string? phash, params DetectedFace[] faces)
    {
        _factory.Faces.NextPhash = phash;
        _factory.Faces.NextSharpness = 300;
        return AddProcessedPhotoAsync(_factory, admin, eventId, collectionId, faces);
    }

    private static async Task MarkAsync(HttpClient admin, Guid eventId, params Guid[] ids)
    {
        var res = await admin.PostAsJsonAsync($"/api/v1/events/{eventId}/photos/highlights", new SetHighlightsRequest(ids, true));
        res.EnsureSuccessStatusCode();
    }

    private static async Task<List<(string Name, List<Guid> Ids)>> StoriesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<HighlightStoryDto>>("/api/v1/public/highlights", Json.Options))!
            .Select(s => (s.Name, s.Photos.Select(p => p.Id).ToList())).ToList();

    [Fact]
    public async Task Guests_see_highlights_by_collection_respecting_hidden_and_the_event_setting()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(admin);
        Guid c0 = ev.Collections[0].Id, c1 = ev.Collections[1].Id;

        var mine = await PhotoAsync(admin, ev.Id, c0, null, Face(1));
        var other = await PhotoAsync(admin, ev.Id, c0, null, Face(5));
        var hidden = await PhotoAsync(admin, ev.Id, c1, null, Face(5));
        var stage = await PhotoAsync(admin, ev.Id, c1, null, Face(6));
        var notHighlight = await PhotoAsync(admin, ev.Id, c1, null, Face(1));
        await MarkAsync(admin, ev.Id, mine, other, hidden, stage);
        (await admin.PatchAsJsonAsync($"/api/v1/photos/{hidden}", new UpdatePhotoRequest(null, true, null))).EnsureSuccessStatusCode();

        var link = await CreateLinkAsync(admin, ev.Id, ShareLinkType.Guest);
        var guest = await GuestClientAsync(_factory, link.Token, UnitVector(1));

        var stories = await StoriesAsync(guest);
        stories.Select(s => s.Name).Should().Equal(ev.Collections[0].Name, ev.Collections[1].Name);
        stories[0].Ids.Should().BeEquivalentTo([mine, other]);
        stories[1].Ids.Should().Equal(new[] { stage }, "hidden photos and non-highlights never show");
        stories.SelectMany(s => s.Ids).Should().NotContain(notHighlight);

        // The couple prefers privacy: Guest-link guests only see highlights they are in.
        (await admin.PutAsJsonAsync($"/api/v1/events/{ev.Id}/highlight-settings", new HighlightSettingsDto(false))).EnsureSuccessStatusCode();
        (await StoriesAsync(guest)).SelectMany(s => s.Ids).Should().Equal(mine);

        // A Full-gallery link sees every visible photo anyway.
        var full = await CreateLinkAsync(admin, ev.Id, ShareLinkType.Full, requireRegistration: false);
        var viewer = await GuestClientAsync(_factory, full.Token, null);
        (await StoriesAsync(viewer)).SelectMany(s => s.Ids).Should().BeEquivalentTo([mine, other, stage]);
    }

    [Fact]
    public async Task Suggestions_skip_blurry_closed_eyes_spare_burst_frames_hidden_and_existing_highlights()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(admin);
        var c = ev.Collections[0].Id;
        var sharp = Face(1) with { Blur = 300, Ear = 0.3f };

        var good = await PhotoAsync(admin, ev.Id, c, "0000000000000000", sharp, Face(2) with { Blur = 250 });
        await PhotoAsync(admin, ev.Id, c, "0000000000000003", Face(1) with { Blur = 200 });          // spare frame of good's burst
        await PhotoAsync(admin, ev.Id, c, "ffffffffffffffff", Face(1) with { Blur = 300, Ear = 0.05f }); // eyes closed
        await PhotoAsync(admin, ev.Id, c, "00000000ffffffff", Face(1) with { Blur = 20 });            // blurry
        await PhotoAsync(admin, ev.Id, c, "ffffffff00000000");                                         // no people
        await PhotoAsync(admin, ev.Id, c, "000000000000ffff", Face(1) with { Blur = 250 });            // looks like good (16 bits), not a burst
        var hidden = await PhotoAsync(admin, ev.Id, c, "0f0f0f0f0f0f0f0f", sharp);
        var marked = await PhotoAsync(admin, ev.Id, c, "f0f0f0f0f0f0f0f0", sharp);
        (await admin.PatchAsJsonAsync($"/api/v1/photos/{hidden}", new UpdatePhotoRequest(null, true, null))).EnsureSuccessStatusCode();
        await MarkAsync(admin, ev.Id, marked);

        var suggestions = await admin.GetFromJsonAsync<List<PhotoDto>>($"/api/v1/events/{ev.Id}/photos/highlight-suggestions", Json.Options);
        suggestions!.Select(p => p.Id).Should().Equal(good);

        var res = await (await admin.PostAsJsonAsync($"/api/v1/events/{ev.Id}/photos/highlights",
            new SetHighlightsRequest([good, marked], true))).Content.ReadFromJsonAsync<Dictionary<string, int>>();
        res!["changed"].Should().Be(1, "marked already was one");
    }
}
