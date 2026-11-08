using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Guests;
using ShaadiLens.Application.People;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;
using ShaadiLens.Infrastructure.Processing;
using ShaadiLens.Tests.Support;
using static ShaadiLens.Tests.Support.FakeFaceEngine;

namespace ShaadiLens.Tests.Guests;

/// <summary>Phase 2d: People clusters — grouping, naming, linking a person to a guest, surviving a re-run.</summary>
[Collection(DbCollection.Name)]
public sealed class PeopleTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(pg);
    private HttpClient _admin = null!;
    private EventDto _event = null!;
    private readonly List<Guid> _ashaPhotos = [];

    public async Task InitializeAsync()
    {
        _admin = await _factory.CreateAdminClientAsync();
        _event = await TestApi.CreateEventAsync(_admin);
        var c = _event.Collections[0].Id;
        // Asha (axis 1) in 4 photos, Bunty (axis 2) in 3, Chacha (axis 3) once → 2 people + 1 ungrouped face.
        for (var i = 0; i < 4; i++)
            _ashaPhotos.Add(await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, c,
                i < 3 ? new[] { FaceWith(UnitVector(1), x: 100), FaceWith(UnitVector(2), x: 600) } : new[] { FaceWith(UnitVector(1)) }));
        await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, c, FaceWith(UnitVector(3)));
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task Cluster()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ClusterJobs>().RunAsync(_event.Id, 0.5f, 3, CancellationToken.None);
    }

    private Task<PeopleDto?> People() => _admin.GetFromJsonAsync<PeopleDto>($"/api/v1/events/{_event.Id}/people", Json.Options);

    [Fact]
    public async Task Faces_are_grouped_into_people_biggest_first()
    {
        (await _admin.PostAsJsonAsync($"/api/v1/events/{_event.Id}/people/run", new RunClusteringRequest(null, null))).StatusCode
            .Should().Be(HttpStatusCode.Accepted);
        _factory.Scheduler.Clustering.Should().Contain(_event.Id);
        (await People())!.Running.Should().BeTrue();

        await Cluster();
        var people = (await People())!;
        people.Running.Should().BeFalse();
        people.UpdatedAt.Should().NotBeNull();
        people.People.Select(p => (p.Number, p.FaceCount, p.PhotoCount)).Should().Equal((1, 4, 4), (2, 3, 3));
        (people.Faces, people.GroupedFaces).Should().Be((8, 7), "Chacha appears once → not grouped");
        people.People[0].Samples.Should().HaveCount(4);
        people.People[0].Samples.Should().OnlyContain(s => s.Face.X < 0.1f, "samples show Asha's face, not Bunty's");
    }

    [Fact]
    public async Task Linking_a_person_gives_the_guest_their_photos_and_survives_a_rerun()
    {
        await Cluster();
        var asha = (await People())!.People.Single(p => p.Number == 1);

        // Riya's selfie doesn't match anybody — the admin knows "Person 1" is Riya.
        var link = await TestApi.CreateLinkAsync(_admin, _event.Id, ShareLinkType.Guest);
        var riya = await TestApi.GuestClientAsync(_factory, link.Token, UnitVector(9), "Riya");
        (await riya.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options))!.Total.Should().Be(0);
        var riyaId = (await _admin.GetFromJsonAsync<List<GuestAdminDto>>($"/api/v1/events/{_event.Id}/guests", Json.Options))!.Single().Id;

        (await _admin.PutAsJsonAsync($"/api/v1/people/{asha.Id}", new UpdatePersonRequest("Riya (bride)", riyaId))).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var mine = await riya.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options);
        mine!.Items.Select(p => p.Id).Should().BeEquivalentTo(_ashaPhotos);

        // A new photo of her arrives later → found through the person's faces (now references).
        var later = await TestApi.AddProcessedPhotoAsync(_factory, _admin, _event.Id, _event.Collections[0].Id, FaceWith(UnitVector(1)));
        (await riya.GetFromJsonAsync<PagedResult<GuestPhotoDto>>("/api/v1/public/me/photos", Json.Options))!.Items
            .Should().Contain(p => p.Id == later);

        // Re-run: Person 1 got a new face, but keeps its name and guest.
        await Cluster();
        var again = (await People())!.People.Single(p => p.Number == 1);
        (again.Name, again.GuestId, again.GuestName, again.FaceCount).Should().Be(("Riya (bride)", riyaId, "Riya", 5));

        // Unlink: her search stops using those faces.
        (await _admin.PutAsJsonAsync($"/api/v1/people/{again.Id}", new UpdatePersonRequest("Riya (bride)", null))).EnsureSuccessStatusCode();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.GuestReferences.CountAsync(r => r.GuestId == riyaId && r.Source == ReferenceSource.ConfirmedMatch)).Should().Be(0);
    }

    [Fact]
    public async Task Persons_can_only_be_linked_to_guests_of_the_same_event()
    {
        await Cluster();
        var person = (await People())!.People[0];
        (await _admin.PutAsJsonAsync($"/api/v1/people/{person.Id}", new UpdatePersonRequest(null, Guid.NewGuid()))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PostAsJsonAsync($"/api/v1/events/{_event.Id}/people/run", new RunClusteringRequest(0.05f, null))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "eps is validated");
    }
}
