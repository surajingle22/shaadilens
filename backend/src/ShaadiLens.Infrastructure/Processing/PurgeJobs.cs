using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Infrastructure.Persistence;

namespace ShaadiLens.Infrastructure.Processing;

/// <summary>
/// Privacy clean-up when an event expires (Event.ExpiresAt), run every hour by Hangfire:
///   - guests: names, phones, emails, push subscriptions, selfie files, reference embeddings, matches → deleted
///   - faces: every face embedding of the event's photos → deleted (face data is biometric)
/// Photos, collections and the couple's Selection picks are kept — the gallery still exists for the admin.
/// Guest links already stop working at ExpiresAt (Event.IsOpen); this removes the data itself.
/// </summary>
public sealed class PurgeJobs(AppDbContext db, IStorageService storage, TimeProvider time, ILogger<PurgeJobs> log)
{
    public const string RecurringJobId = "purge-expired-events";

    [Queue("default")]
    [AutomaticRetry(Attempts = 2)]
    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var events = await db.Events.Where(e => e.ExpiresAt != null && e.ExpiresAt <= now && e.PurgedAt == null).ToListAsync(ct);
        foreach (var ev in events) await PurgeAsync(ev, now, ct);
    }

    private async Task PurgeAsync(Event ev, DateTimeOffset now, CancellationToken ct)
    {
        // Set-based deletes: references and matches go with the guests (FK cascade), matches also with faces.
        var guests = await db.Guests.Where(g => g.EventId == ev.Id).ExecuteDeleteAsync(ct);
        var faces = await db.Faces.Where(f => f.EventId == ev.Id).ExecuteDeleteAsync(ct);
        await storage.DeletePrefixAsync($"events/{ev.Id}/selfies", ct);

        ev.PurgedAt = now;
        ev.FaceSearchEnabled = false;
        db.AuditLogs.Add(new AuditLog { Actor = "system", Action = "event_purged", EntityId = ev.Id.ToString(), Details = $"{guests} guests, {faces} faces" });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Purged expired event {EventId}: {Guests} guests and {Faces} face embeddings deleted", ev.Id, guests, faces);
    }
}
