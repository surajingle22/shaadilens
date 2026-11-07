using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ShaadiLens.Api.Controllers;
using ShaadiLens.Application.Events;
using ShaadiLens.Tests.Support;

namespace ShaadiLens.Tests.Api;

[Collection(DbCollection.Name)]
public sealed class AuthAndEventsTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Ping_and_liveness_need_no_login()
    {
        var client = _factory.CreateClient();
        (await client.GetFromJsonAsync<PingResponse>("/api/v1/ping"))!.Name.Should().Be("ShaadiLens API");
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_endpoints_require_login()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/api/v1/events")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/hangfire")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFactory.AdminEmail, password = "nope" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        res.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Login_sets_httponly_cookie_and_unlocks_admin_api()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFactory.AdminEmail.ToUpperInvariant(), password = ApiFactory.AdminPassword });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.GetValues("Set-Cookie").Single().Should().Contain("sl_admin=").And.Contain("httponly").And.Contain("samesite=strict");

        // Demo event is seeded on an empty database.
        var events = await client.GetFromJsonAsync<List<EventSummaryDto>>("/api/v1/events", Json.Options);
        events.Should().Contain(e => e.Slug == "demo-wedding");
        (await client.GetAsync("/hangfire")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_event_gets_default_collections_and_unique_slug()
    {
        var client = await _factory.CreateAdminClientAsync();
        var name = $"Suraj & Priya's Wedding {Guid.NewGuid():N}"[..40];

        var first = await (await client.PostAsJsonAsync("/api/v1/events", NewEvent(name))).Content.ReadFromJsonAsync<EventDto>(Json.Options);
        var second = await (await client.PostAsJsonAsync("/api/v1/events", NewEvent(name))).Content.ReadFromJsonAsync<EventDto>(Json.Options);

        first!.Collections.Select(c => c.Name).Should().Equal("Haldi", "Mehendi", "Sangeet", "Wedding", "Reception");
        first.Slug.Should().StartWith("suraj-priya-s-wedding");
        second!.Slug.Should().Be(first.Slug + "-2");
        first.IsPublished.Should().BeFalse("nothing is visible to guests before publishing");
    }

    [Fact]
    public async Task Invalid_event_returns_validation_problem()
    {
        var client = await _factory.CreateAdminClientAsync();
        var res = await client.PostAsJsonAsync("/api/v1/events", NewEvent("") with { ThemeColor = "red" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Name").And.Contain("ThemeColor");
    }

    [Fact]
    public async Task Collections_can_be_added_reordered_and_only_empty_ones_deleted()
    {
        var client = await _factory.CreateAdminClientAsync();
        var ev = await (await client.PostAsJsonAsync("/api/v1/events", NewEvent("Collections test"))).Content.ReadFromJsonAsync<EventDto>(Json.Options);

        var added = await (await client.PostAsJsonAsync($"/api/v1/events/{ev!.Id}/collections", new SaveCollectionRequest("Cocktail", false)))
            .Content.ReadFromJsonAsync<CollectionDto>(Json.Options);
        added!.SortOrder.Should().Be(5);

        var newOrder = ev.Collections.Select(c => c.Id).Reverse().Prepend(added.Id).ToList();
        (await client.PutAsJsonAsync($"/api/v1/events/{ev.Id}/collections/order", newOrder)).EnsureSuccessStatusCode();
        var reloaded = await client.GetFromJsonAsync<EventDto>($"/api/v1/events/{ev.Id}", Json.Options);
        reloaded!.Collections.Select(c => c.Id).Should().Equal(newOrder);

        (await client.DeleteAsync($"/api/v1/events/{ev.Id}/collections/{added.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static SaveEventRequest NewEvent(string name) =>
        new(name, new DateOnly(2026, 12, 12), "Pune", "#b0004a", null, false, null, true, null);
}
