using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShaadiLens.Application.Events;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Infrastructure.Persistence;

/// <summary>Applies EF migrations at startup and seeds a demo event on an empty database.</summary>
public sealed class DatabaseInitializer(AppDbContext db, ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            log.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
        }

        if (!await db.Events.AnyAsync(ct)) await SeedDemoAsync(ct);
    }

    private async Task SeedDemoAsync(CancellationToken ct)
    {
        var ev = new Event
        {
            Name = "Demo Wedding",
            Slug = "demo-wedding",
            Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            Venue = "Pune",
            Collections = EventService.DefaultCollections
                .Select((name, i) => new Collection { Name = name, SortOrder = i }).ToList(),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded demo event {EventId} with {Count} collections", ev.Id, ev.Collections.Count);
    }
}
