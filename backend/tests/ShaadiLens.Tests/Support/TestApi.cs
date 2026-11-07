using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ShaadiLens.Application.Events;
using ShaadiLens.Application.Faces;
using ShaadiLens.Application.Sharing;
using ShaadiLens.Application.Uploads;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Processing;

namespace ShaadiLens.Tests.Support;

/// <summary>Small helpers that drive the API like the admin UI would.</summary>
public static class TestApi
{
    public static async Task<EventDto> CreateEventAsync(HttpClient admin, bool publish = true)
    {
        var res = await admin.PostAsJsonAsync("/api/v1/events",
            new SaveEventRequest($"Test {Guid.NewGuid():N}"[..20], new DateOnly(2026, 12, 12), "Pune", null, null, false, null, true, null));
        res.EnsureSuccessStatusCode();
        var ev = (await res.Content.ReadFromJsonAsync<EventDto>(Json.Options))!;
        if (publish) (await admin.PostAsJsonAsync($"/api/v1/events/{ev.Id}/publish", new { isPublished = true })).EnsureSuccessStatusCode();
        return ev;
    }

    public static async Task<ShareLinkDto> CreateLinkAsync(HttpClient admin, Guid eventId, ShareLinkType type,
        bool requireRegistration = true, RegistrationFields fields = RegistrationFields.Name, bool downloads = true)
    {
        var res = await admin.PostAsJsonAsync($"/api/v1/events/{eventId}/share-links",
            new SaveShareLinkRequest(type, null, requireRegistration, fields, downloads, downloads, null, true), Json.Options);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ShareLinkDto>(Json.Options))!;
    }

    /// <summary>Uploads a generated JPEG and runs both pipeline jobs with the given fake faces.</summary>
    public static async Task<Guid> AddProcessedPhotoAsync(ApiFactory factory, HttpClient admin, Guid eventId, Guid collectionId, params DetectedFace[] faces)
    {
        var bytes = TestImages.CameraJpeg(800, 600, orientation: 1, seed: Guid.NewGuid().ToString("N"));
        var init = (await (await admin.PostAsJsonAsync($"/api/v1/events/{eventId}/uploads",
            new InitUploadRequest(collectionId, "p.jpg", bytes.Length))).Content.ReadFromJsonAsync<InitUploadResponse>(Json.Options))!;
        for (var i = 0; i < init.TotalChunks; i++)
        {
            var start = i * init.ChunkSize;
            var content = new ByteArrayContent(bytes, start, Math.Min(init.ChunkSize, bytes.Length - start));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await admin.PutAsync($"/api/v1/uploads/{init.UploadId}/chunks/{i}", content)).EnsureSuccessStatusCode();
        }
        var done = (await (await admin.PostAsync($"/api/v1/uploads/{init.UploadId}/complete", null))
            .Content.ReadFromJsonAsync<CompleteUploadResponse>(Json.Options))!;

        factory.Faces.NextFaces = [.. faces];
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<PhotoJobs>();
        await jobs.PrepareAsync(done.PhotoId, null, CancellationToken.None);
        await jobs.DetectFacesAsync(done.PhotoId, null, CancellationToken.None);
        return done.PhotoId;
    }

    /// <summary>Registers through a share link (multipart form, like the guest page). Returns the raw response.</summary>
    public static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string token, string? name = "Guest",
        bool consent = true, bool withSelfie = true, string? pinTicket = null)
    {
        var form = new MultipartFormDataContent();
        if (name is not null) form.Add(new StringContent(name), "name");
        form.Add(new StringContent(consent ? "true" : "false"), "consent");
        if (pinTicket is not null) form.Add(new StringContent(pinTicket), "pinTicket");
        if (withSelfie)
        {
            var selfie = new ByteArrayContent(TestImages.CameraJpeg(600, 800, orientation: 1, seed: "selfie"));
            selfie.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(selfie, "selfie", "selfie.jpg");
        }
        return client.PostAsync($"/api/v1/public/links/{token}/register", form);
    }

    /// <summary>Registers and returns an HttpClient carrying the guest's Bearer token.</summary>
    public static async Task<HttpClient> GuestClientAsync(ApiFactory factory, string token, float[]? selfieEmbedding, string name = "Guest")
    {
        if (selfieEmbedding is not null) factory.Faces.NextSelfie = FakeFaceEngine.GoodSelfie(selfieEmbedding);
        var client = factory.CreateClient();
        var res = await RegisterAsync(client, token, name, withSelfie: selfieEmbedding is not null);
        res.EnsureSuccessStatusCode();
        var login = await res.Content.ReadFromJsonAsync<LoginBody>(Json.Options);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    public sealed record LoginBody(string Token);
}
