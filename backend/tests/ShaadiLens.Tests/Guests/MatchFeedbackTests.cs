using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;

namespace ShaadiLens.Tests.Guests;

/// <summary>
/// Phase 2a: reverse matching (photos uploaded after registration), "That's me" / "Not me", "Find more",
/// extra selfies, admin Match Review and the threshold preview.
///
/// Asha = axis 1. Default thresholds: Auto ≥ 0.42, Probable ≥ 0.34.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class MatchFeedbackTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(pg);
    private HttpClient _admin = null!;
    private EventDto _event = null!;
    private Guid _haldi;

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminClientAsync();
        _event = await TestApi.CreateEventAsync(_admin);
        _haldi = _event.Collections[0].Id;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<Guid> Photo(params Application.Faces.DetectedFace[] faces) =>
        TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, _haldi, faces);

    private async Task<HttpClient> Asha()
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        return await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(1), "Asha");
    }

    private static async Task<List<Guid>> Mine(HttpClient guest) =>
        (await guest.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options))!.Items.Select(p => p.Id).ToList();

    private static async Task<List<Guid>> Maybe(HttpClient guest) =>
        (await guest.GetFromJsonAsync<List<GuestPhotoDto>>("/api/v1/public/me/probable", Json.Options))!.Select(p => p.Id).ToList();

    [Fact]
    public async Task Photos_uploaded_after_registration_reach_the_guest_by_themselves()
    {
        var asha = await Asha();
        (await Mine(asha)).Should().BeEmpty("no photos yet");

        var solo = await Photo(FaceWith(UnitVector(1)));
        var maybe = await Photo(FaceWith(WithSimilarity(1, 0.38f, 9)));
        var other = await Photo(FaceWith(UnitVector(3)));

        (await Mine(asha)).Should().Equal([solo], "reverse matching ran right after face detection");
        (await Maybe(asha)).Should().Equal([maybe]);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Photos.Where(p => p.Id == solo || p.Id == other).Select(p => p.Status).ToListAsync())
            .Should().AllBeEquivalentTo(PhotoStatus.Matched);
    }

    [Fact]
    public async Task Thats_me_adds_a_reference_and_find_more_uses_it_until_rejected()
    {
        var asha = await Asha();
        var side = await Photo(FaceWith(WithSimilarity(1, 0.38f, 9)));  // "might be Asha" (side profile)
        var later = await Photo(FaceWith(UnitVector(9)));               // looks like the side profile, not the selfie
        (await Mine(asha)).Should().BeEmpty();

        (await asha.PostAsync($"/api/v1/public/me/matches/{side}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Mine(asha)).Should().Equal([side], "confirmed photos show in 'Your photos'");
        (await Maybe(asha)).Should().BeEmpty();

        var found = await (await asha.PostAsync("/api/v1/public/me/find-more", null)).Content.ReadFromJsonAsync<MatchSummary>(Json.Options);
        found!.Auto.Should().Be(1, "the confirmed face (0.93 similar to 'later') is now a reference");
        (await Mine(asha)).Should().BeEquivalentTo([side, later]);

        // Changed her mind: "Not me" on the side profile removes it AND the reference it added.
        (await asha.PostAsync($"/api/v1/public/me/matches/{side}/reject", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await asha.PostAsync("/api/v1/public/me/find-more", null)).EnsureSuccessStatusCode();
        (await Mine(asha)).Should().BeEmpty("'later' was only found through the rejected face");
        (await Maybe(asha)).Should().BeEmpty("rejected photos never come back");
    }

    [Fact]
    public async Task Not_me_survives_reprocessing_the_photo()
    {
        var asha = await Asha();
        var solo = await Photo(FaceWith(UnitVector(1)));
        (await asha.PostAsync($"/api/v1/public/me/matches/{solo}/reject", null)).EnsureSuccessStatusCode();
        (await Mine(asha)).Should().BeEmpty();

        // Admin re-runs face detection: faces (and the match rows on them) are recreated.
        _factory.Faces.NextFaces = [FaceWith(UnitVector(1))];
        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PhotoJobs>().DetectFacesAsync(solo, null, CancellationToken.None);

        (await Mine(asha)).Should().BeEmpty("the guest's 'Not me' is restored after re-detection");
        await using (var scope = _factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().GuestMatches.SingleAsync(m => m.PhotoId == solo))
                .Status.Should().Be(MatchStatus.Rejected);
    }

    [Fact]
    public async Task Guests_can_only_decide_on_their_own_matches()
    {
        var asha = await Asha();
        var other = await Photo(FaceWith(UnitVector(3)));
        (await asha.PostAsync($"/api/v1/public/me/matches/{other}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await asha.PostAsync($"/api/v1/public/me/matches/{Guid.NewGuid()}/reject", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var anonymous = await TestApi.GuestClientAsync(_factory,
            (await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Full, requireRegistration: false)).Token, null);
        (await anonymous.PostAsync($"/api/v1/public/me/matches/{other}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Extra_selfie_finds_more_photos_and_is_limited()
    {
        var asha = await Asha();
        var glasses = await Photo(FaceWith(UnitVector(5))); // a look the first selfie doesn't match
        (await Mine(asha)).Should().BeEmpty();

        var summary = await (await AddSelfie(asha, UnitVector(5))).Content.ReadFromJsonAsync<MatchSummary>(Json.Options);
        summary!.Auto.Should().Be(1);
        (await Mine(asha)).Should().Equal([glasses]);
        (await asha.GetFromJsonAsync<GuestSessionDto>("/api/v1/public/me", Json.Options))!.SelfieCount.Should().Be(2);

        (await AddSelfie(asha, UnitVector(6))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AddSelfie(asha, UnitVector(7))).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"max {GuestAccessService.MaxSelfies} selfies");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.GuestReferences.CountAsync(r => r.Source == ReferenceSource.Selfie && r.ImageKey != null &&
                                                  db.Guests.Any(g => g.Id == r.GuestId && g.EventId == _event.Id)))
            .Should().Be(3, "every selfie is stored with its image for Match Review");
    }

    [Fact]
    public async Task Admin_reviews_matches_and_previews_thresholds()
    {
        var asha = await Asha();
        var solo = await Photo(FaceWith(UnitVector(1)));                                              // 1.00
        var group = await Photo(FaceWith(WithSimilarity(1, 0.50f, 9), x: 100), FaceWith(UnitVector(2), x: 600)); // 0.50
        var maybe = await Photo(FaceWith(WithSimilarity(1, 0.38f, 9)));                               // 0.38
        var guestId = (await _admin.GetFromJsonAsync<List<GuestAdminDto>>($"/api/v1/events/{_event.Id}/guests", Json.Options))!.Single().Id;

        var review = await _admin.GetFromJsonAsync<GuestReviewDto>($"/api/v1/guests/{guestId}/matches", Json.Options);
        review!.Matches.Select(m => (m.PhotoId, m.Status)).Should().Equal(
            (maybe, MatchStatus.Probable), (solo, MatchStatus.Auto), (group, MatchStatus.Auto));
        review.Matches.Single(m => m.PhotoId == group).Face.X.Should().BeApproximately(100f / 2048, 0.001f, "the face box of Asha, not Bunty");
        review.Selfies.Should().HaveCount(1);
        var selfie = await _admin.GetAsync($"/api/v1/guest-references/{review.Selfies[0]}/image");
        selfie.StatusCode.Should().Be(HttpStatusCode.OK);
        selfie.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await asha.GetAsync($"/api/v1/guest-references/{review.Selfies[0]}/image")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Admin approves the "maybe" → Asha sees it.
        var maybeMatch = review.Matches.Single(m => m.PhotoId == maybe).Id;
        (await _admin.PatchAsJsonAsync($"/api/v1/matches/{maybeMatch}", new SetMatchStatusRequest(MatchStatus.Confirmed), Json.Options))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Mine(asha)).Should().Contain(maybe);
        (await _admin.PatchAsJsonAsync($"/api/v1/matches/{maybeMatch}", new SetMatchStatusRequest(MatchStatus.Auto), Json.Options))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "admin can only confirm or reject");

        // Preview: solo and group move with the threshold; the confirmed one doesn't count (thresholds can't change it).
        var at45 = await _admin.GetFromJsonAsync<ThresholdPreviewDto>($"/api/v1/events/{_event.Id}/face-settings/preview?threshold=0.45", Json.Options);
        at45!.Matches.Should().Be(2);
        at45.Confirmed.Should().Be(1);
        at45.Guests.Should().Be(1);
        at45.Buckets.Sum(b => b.Count).Should().Be(2);
        // Query expansion at work: the confirmed face (0.38·e1 + 0.92·e9) is 0.99 similar to Asha's face in the
        // group photo, so the group photo is now a near-certain match instead of 0.50.
        at45.Buckets.Should().OnlyContain(b => b.Score >= 0.98f);
        var at30 = await _admin.GetFromJsonAsync<ThresholdPreviewDto>($"/api/v1/events/{_event.Id}/face-settings/preview?threshold=0.30", Json.Options);
        at30!.Matches.Should().Be(2, "Bunty's face never counts for Asha, and the confirmed pair is counted separately");

        // "Apply to all guests" queues a background re-match; the job itself runs fine.
        (await _admin.PostAsync($"/api/v1/events/{_event.Id}/rematch", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Scheduler.Rematch.Should().Contain(_event.Id);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MatchJobs>().RematchEventAsync(_event.Id, CancellationToken.None);
        (await Mine(asha)).Should().BeEquivalentTo([solo, group, maybe]);
    }

    private Task<HttpResponseMessage> AddSelfie(HttpClient guest, float[] embedding)
    {
        _factory.Faces.NextSelfie = GoodSelfie(embedding);
        var form = new MultipartFormDataContent();
        var selfie = new ByteArrayContent(TestImages.CameraJpeg(600, 800, orientation: 1, seed: Guid.NewGuid().ToString("N")));
        selfie.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(selfie, "selfie", "selfie.jpg");
        return guest.PostAsync("/api/v1/public/me/selfie", form);
    }
}
