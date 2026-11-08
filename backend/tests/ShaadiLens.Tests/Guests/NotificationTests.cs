using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Api.Controllers;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Notifications;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;

namespace ShaadiLens.Tests.Guests;

/// <summary>Phase 2b: "Notify me" (Web Push + email, debounced), SignalR live updates, admin Settings.</summary>
[Collection(DbCollection.Name)]
public sealed class NotificationTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Subscription = """{"endpoint":"https://push.example.com/send/abc","expirationTime":null,"keys":{"p256dh":"BNcR","auth":"tBH"}}""";

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

    private async Task<(HttpClient Client, Guid GuestId, string LinkToken)> Register(string name, string? email = null)
    {
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest,
            fields: email is null ? RegistrationFields.Name : RegistrationFields.Name | RegistrationFields.Email);
        _factory.Faces.NextSelfie = GoodSelfie(UnitVector(1));
        var form = new MultipartFormDataContent { { new StringContent(name), "name" }, { new StringContent("true"), "consent" } };
        if (email is not null) form.Add(new StringContent(email), "email");
        var selfie = new ByteArrayContent(TestImages.CameraJpeg(600, 800, orientation: 1, seed: "selfie"));
        selfie.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(selfie, "selfie", "selfie.jpg");
        var client = _factory.CreateClient();
        var res = await client.PostAsync($"/api/v1/public/links/{link.Token}/register", form);
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<TestApi.LoginBody>(Json.Options))!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var guestId = (await _admin.GetFromJsonAsync<List<GuestAdminDto>>($"/api/v1/events/{_event.Id}/guests", Json.Options))!
            .Single(g => g.Name == name).Id;
        return (client, guestId, link.Token);
    }

    private static StringContent Body(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private async Task Send(Guid guestId, bool force = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotifyJobs>().SendAsync(guestId, force, CancellationToken.None);
    }

    private async Task<Domain.Entities.Guest> Guest(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Guests.AsNoTracking().SingleAsync(g => g.Id == id);
    }

    [Fact]
    public async Task A_burst_of_new_photos_gives_one_push_with_the_count()
    {
        var (asha, guestId, token) = await Register("Asha");
        var status = await (await asha.PutAsync("/api/v1/public/me/notifications", Body($$"""{"subscription":{{Subscription}},"email":true}""")))
            .Content.ReadFromJsonAsync<NotificationStatusDto>(Json.Options);
        status!.PushOn.Should().BeTrue();
        status.EmailOn.Should().BeFalse("she gave no email address");
        status.PushPublicKey.Should().Be(_factory.Push.PublicKey);

        await Photo(FaceWith(UnitVector(1)));
        var scheduled = (await Guest(guestId)).NotifyScheduledAt;
        scheduled.Should().NotBeNull("a debounced job is waiting");
        await Photo(FaceWith(UnitVector(1)));
        (await Guest(guestId)).NotifyScheduledAt.Should().Be(scheduled, "the second photo joins the waiting job");

        await Send(guestId);
        _factory.Push.Sent.Should().ContainSingle();
        var msg = _factory.Push.Sent[0].Message;
        msg.Title.Should().Be("2 new photos of you!");
        msg.Url.Should().EndWith($"/g/{token}");
        var after = await Guest(guestId);
        after.LastNotifiedAt.Should().NotBeNull();
        after.NotifyScheduledAt.Should().BeNull();

        await Send(guestId);
        _factory.Push.Sent.Should().HaveCount(1, "nothing new since the last message");

        // Next photo: scheduled no earlier than 10 minutes after the last message.
        await Photo(FaceWith(UnitVector(1)));
        (await Guest(guestId)).NotifyScheduledAt.Should().BeOnOrAfter(after.LastNotifiedAt!.Value + NotifyJobs.MinInterval);
    }

    [Fact]
    public async Task Unsubscribed_phones_are_forgotten_and_admin_can_resend()
    {
        var (asha, guestId, _) = await Register("Asha");
        (await _admin.PostAsync($"/api/v1/guests/{guestId}/notify", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "notifications are off");
        (await asha.PutAsync("/api/v1/public/me/notifications", Body($$"""{"subscription":{{Subscription}},"email":false}"""))).EnsureSuccessStatusCode();
        (await _admin.PostAsync($"/api/v1/guests/{guestId}/notify", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await Send(guestId, force: true);
        _factory.Push.Sent.Should().ContainSingle().Which.Message.Title.Should().StartWith("Your photos from");

        _factory.Push.NextResult = PushResult.Gone;
        await Send(guestId, force: true);
        (await Guest(guestId)).PushSubscriptionJson.Should().BeNull("the browser unsubscribed (410 Gone)");
    }

    [Fact]
    public async Task Email_needs_an_address_and_an_opt_in()
    {
        var (riya, guestId, _) = await Register("Riya", "riya@example.com");
        await Photo(FaceWith(UnitVector(1)));
        await Send(guestId, force: true);
        _factory.Email.Sent.Should().BeEmpty("she didn't ask for emails");

        var status = await (await riya.PutAsync("/api/v1/public/me/notifications", Body("""{"subscription":null,"email":true}""")))
            .Content.ReadFromJsonAsync<NotificationStatusDto>(Json.Options);
        status!.EmailOn.Should().BeTrue();
        await Send(guestId, force: true);
        _factory.Email.Sent.Should().ContainSingle().Which.To.Should().Be("riya@example.com");

        (await riya.DeleteAsync("/api/v1/public/me/notifications")).EnsureSuccessStatusCode();
        await Send(guestId, force: true);
        _factory.Email.Sent.Should().HaveCount(1, "turned off again");
    }

    [Fact]
    public async Task Bad_subscriptions_are_refused()
    {
        var (asha, _, _) = await Register("Asha");
        (await asha.PutAsync("/api/v1/public/me/notifications", Body("""{"subscription":{"endpoint":"http://insecure.example"},"email":false}""")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Guest_gets_a_live_new_photos_message_over_signalr()
    {
        var (asha, _, _) = await Register("Asha");
        var token = asha.DefaultRequestHeaders.Authorization!.Parameter!;
        await using var hub = Hub(token);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<NewMatches>("NewMatchesForYou", m => received.TrySetResult(m.Count));
        await hub.StartAsync();

        await Photo(FaceWith(UnitVector(1)));
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(1);

        await using var anonymous = Hub(null);
        var connect = () => anonymous.StartAsync();
        await connect.Should().ThrowAsync<HttpRequestException>("the hub needs a login");
    }

    [Fact]
    public async Task Settings_show_status_generate_vapid_keys_and_send_a_test_email()
    {
        var settings = await _admin.GetFromJsonAsync<SettingsDto>("/api/v1/settings", Json.Options);
        settings!.Push.Configured.Should().BeTrue();

        var keys = await (await _admin.PostAsync("/api/v1/settings/vapid-keys", null)).Content.ReadFromJsonAsync<VapidKeysResponse>(Json.Options);
        keys!.PublicKey.Should().HaveLength(87, "65-byte P-256 public key, base64url");
        keys.EnvLines.Should().Contain("VAPID_PRIVATE=");

        (await _admin.PostAsJsonAsync("/api/v1/settings/test-email", new TestEmailRequest("me@example.com"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Email.Sent.Should().ContainSingle(e => e.To == "me@example.com");
        (await _factory.CreateClient().GetAsync("/api/v1/settings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private HubConnection Hub(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/events"), o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling; // the in-memory test server has no real WebSockets
            if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    private sealed record NewMatches(int Count);
}
