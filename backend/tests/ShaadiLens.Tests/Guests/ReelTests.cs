using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Reels;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Reels;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;
using static ShaadiLens.Tests.Support.TestApi;

namespace ShaadiLens.Tests.Guests;

/// <summary>Picking photos and the ffmpeg command: plain functions.</summary>
public sealed class ReelPlanningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 12, 12, 10, 0, 0, TimeSpan.Zero);

    private static ReelCandidate C(int n, Guid collection, float px = 200, float sharp = 200, float? ear = 0.3f, Guid? burst = null) =>
        new(new Guid(n, 0, 0, new byte[8]), collection, T0.AddMinutes(n), 0.5f, 0.4f, px, sharp, ear, 0, false, false, burst);

    [Fact]
    public void Picks_the_best_one_per_burst_spreads_over_collections_and_plays_in_time_order()
    {
        Guid haldi = Guid.NewGuid(), wedding = Guid.NewGuid(), burst = Guid.NewGuid();
        var candidates = new[]
        {
            C(1, haldi, px: 400),
            C(2, haldi, px: 500, burst: burst),
            C(3, haldi, px: 500, ear: 0.05f, burst: burst), // same moment, eyes closed → loses
            C(4, haldi, px: 300),
            C(5, wedding, px: 60, sharp: 40),                // tiny + soft, but the only wedding photo
        };

        var shots = ReelPlanner.Pick(candidates, 3);

        shots.Select(s => s.PhotoId).Should().Equal(candidates[0].PhotoId, candidates[1].PhotoId, candidates[4].PhotoId);
    }

    [Fact]
    public void Score_prefers_big_sharp_open_eyed_faces()
    {
        var c = Guid.NewGuid();
        ReelPlanner.Score(C(1, c, px: 500)).Should().BeGreaterThan(ReelPlanner.Score(C(2, c, px: 80)));
        ReelPlanner.Score(C(1, c, sharp: 300)).Should().BeGreaterThan(ReelPlanner.Score(C(2, c, sharp: 30)));
        ReelPlanner.Score(C(1, c)).Should().BeGreaterThan(ReelPlanner.Score(C(2, c, ear: 0.08f)) * 2);
    }

    [Fact]
    public void Crop_is_9_by_16_around_the_face_and_stays_inside_the_photo()
    {
        // Landscape 6000x4000, face on the far right → crop is pushed against the right edge.
        var c = ReelVideo.Crop(6000, 4000, 0.95f, 0.3f);
        (c.Width, c.Height).Should().Be((2250, 4000));
        c.X.Should().Be(6000 - 2250);
        c.Y.Should().Be(0);
        c.FocusX.Should().BeApproximately((0.95f * 6000 - 3750) / 2250, 1e-4f);

        // Very tall photo: full width, cut vertically with the face ~40% from the top.
        var tall = ReelVideo.Crop(1000, 3000, 0.5f, 0.5f);
        (tall.X, tall.Width, tall.Height).Should().Be((0, 1000, 1778));
        tall.Y.Should().Be((int)Math.Round(1500 - 1778 * 0.4));
    }

    [Fact]
    public void Ffmpeg_arguments_chain_zooms_and_crossfades_and_use_invariant_numbers()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // "2,4" would break ffmpeg
        try
        {
            var frames = new[] { ("/f0.jpg", 0.5f, 0.4f), ("/f1.jpg", 0.25f, 0.4f), ("/f2.jpg", 0.75f, 0.5f) };
            var args = ReelVideo.Arguments(frames, "/t.png", "/w.png", "/m.mp3", "/out.mp4", 3.0, 0.6);
            var graph = args[args.ToList().IndexOf("-filter_complex") + 1];

            graph.Split("zoompan=").Should().HaveCount(4);
            graph.Should().Contain("[v0][v1]xfade=transition=fade:duration=0.6:offset=2.4[x1]")
                .And.Contain("[x1][v2]xfade=transition=fade:duration=0.6:offset=4.8[x2]")
                .And.Contain("0.25*iw-iw/zoom/2")
                .And.Contain("[5:a]atrim=0:7.8");                      // 3 photos · 3 s − 2 · 0.6 s
            args.Should().ContainInOrder("-t", "7.8", "-movflags", "+faststart", "/out.mp4");
            args.Should().Contain("[aout]");

            var silent = ReelVideo.Arguments(frames, "/t.png", "/w.png", null, "/out.mp4", 3.0, 0.6);
            silent.Should().NotContain("[aout]").And.NotContain("-stream_loop");
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Overlay_text_is_cleaned()
    {
        FfmpegReelRenderer.Clean("@/etc/passwd").Should().Be("/etc/passwd");
        FfmpegReelRenderer.Clean("Riya %[pixel:p{0,0}]\n").Should().Be("Riya [pixel:p{0,0}]");
        FfmpegReelRenderer.Clean(new string('a', 80)).Should().HaveLength(61);
    }
}

[Collection(DbCollection.Name)]
public sealed class ReelTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg);
    public void Dispose() => _factory.Dispose();

    private async Task<(HttpClient admin, Guid eventId, HttpClient guest)> SetupAsync(int photos)
    {
        var admin = await _factory.CreateAdminClientAsync();
        var ev = await CreateEventAsync(admin);
        var link = await CreateLinkAsync(admin, ev.Id, ShareLinkType.Guest);
        var guest = await GuestClientAsync(_factory, link.Token, UnitVector(1), "Riya");
        for (var i = 0; i < photos; i++)
            await AddProcessedPhotoAsync(_factory, admin, ev.Id, ev.Collections[i % 2].Id, Face(1, sizePx: 200), Face(7, x: 900));
        return (admin, ev.Id, guest);
    }

    private async Task RenderAsync(Guid reelId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReelJobs>().RenderAsync(reelId, CancellationToken.None);
    }

    private static async Task<ReelStateDto> StateAsync(HttpClient guest) =>
        (await guest.GetFromJsonAsync<ReelStateDto>("/api/v1/public/me/reel", Json.Options))!;

    [Fact]
    public async Task Make_render_watch_and_download_a_reel()
    {
        var (_, _, guest) = await SetupAsync(4);

        var state = await StateAsync(guest);
        state.Should().Match<ReelStateDto>(s => s.Enabled && s.Available && s.PhotosAvailable == 4 && s.RemainingToday == 3 && s.Latest == null);

        var res = await guest.PostAsync("/api/v1/public/me/reel", null);
        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var reel = (await res.Content.ReadFromJsonAsync<ReelDto>(Json.Options))!;
        reel.Status.Should().Be(ReelStatus.Queued);
        reel.PhotoCount.Should().Be(4);
        reel.DurationSeconds.Should().Be(10.2f); // 4 · 3 s − 3 · 0.6 s
        _factory.Scheduler.Reels.Should().Contain(reel.Id);

        (await guest.PostAsync("/api/v1/public/me/reel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "one at a time");

        await RenderAsync(reel.Id);
        var request = _factory.Reels.Requests.Single();
        request.Shots.Should().HaveCount(4);
        request.Subtitle.Should().Be("Riya's highlights");
        request.Shots.Should().OnlyContain(s => s.FaceX > 0 && s.FaceX < 0.2, "the crop is centred on Riya's face (x≈100-300 of 2048), not the other guest");

        state = await StateAsync(guest);
        state.Latest!.Status.Should().Be(ReelStatus.Ready);
        state.RemainingToday.Should().Be(2);
        var video = await guest.GetAsync(state.Latest.Url);
        video.StatusCode.Should().Be(HttpStatusCode.OK);
        video.Content.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        (await video.Content.ReadAsStringAsync()).Should().Be("fake mp4 bytes");
        var download = await guest.GetAsync(state.Latest.DownloadUrl);
        download.Content.Headers.ContentDisposition!.FileName.Should().EndWith("-highlights.mp4");
    }

    [Fact]
    public async Task A_new_reel_replaces_the_old_file_and_the_daily_limit_applies()
    {
        var (_, _, guest) = await SetupAsync(3);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var reel = (await (await guest.PostAsync("/api/v1/public/me/reel", null)).Content.ReadFromJsonAsync<ReelDto>(Json.Options))!;
            await RenderAsync(reel.Id);
            ids.Add(reel.Id);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var reels = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Reels.AsNoTracking()
                .Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
            reels[ids[0]].Status.Should().Be(ReelStatus.Replaced);
            reels[ids[0]].StorageKey.Should().BeNull();
            reels[ids[2]].Status.Should().Be(ReelStatus.Ready);
        }
        _factory.Reels.Requests.Select(r => r.OutputPath).Take(2).Should().OnlyContain(p => !File.Exists(p), "old reels are deleted");

        var state = await StateAsync(guest);
        state.Available.Should().BeFalse();
        state.Reason.Should().Contain("maximum");
        (await guest.PostAsync("/api/v1/public/me/reel", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Failed_render_is_reported_and_does_not_count()
    {
        var (_, _, guest) = await SetupAsync(3);
        _factory.Reels.Fail = true;
        var reel = (await (await guest.PostAsync("/api/v1/public/me/reel", null)).Content.ReadFromJsonAsync<ReelDto>(Json.Options))!;
        await RenderAsync(reel.Id);

        var state = await StateAsync(guest);
        state.Latest!.Status.Should().Be(ReelStatus.Failed);
        state.Latest.Error.Should().NotContain("ffmpeg", "guests get a friendly message, the details stay in the log");
        state.Available.Should().BeTrue();
        state.RemainingToday.Should().Be(3);
    }

    [Fact]
    public async Task Rules_too_few_photos_reels_off_and_no_selfie()
    {
        var (admin, eventId, guest) = await SetupAsync(2);
        var res = await guest.PostAsync("/api/v1/public/me/reel", null);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StateAsync(guest)).Reason.Should().Contain("at least 3 photos");

        (await admin.PutAsJsonAsync($"/api/v1/events/{eventId}/reel-settings", new ReelSettingsDto(false, 12))).EnsureSuccessStatusCode();
        (await guest.PostAsync("/api/v1/public/me/reel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync($"/api/v1/events/{eventId}/reel-settings", new ReelSettingsDto(true, 50)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var full = await CreateLinkAsync(admin, eventId, ShareLinkType.Full, requireRegistration: false);
        var viewer = await GuestClientAsync(_factory, full.Token, null);
        (await viewer.PostAsync("/api/v1/public/me/reel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
