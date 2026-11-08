using System.Net;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Domain.Entities;
using ShaadiLens.Domain.Enums;
using ShaadiLens.Infrastructure.Persistence;

namespace ShaadiLens.Infrastructure.Notifications;

/// <summary>
/// "12 new photos of you" by Web Push and email, debounced:
///
///   photo 1 matches Asha ─┐
///   photo 2 matches Asha ─┼─► one job scheduled at max(now + 1 min, last message + 10 min)
///   photo 3 matches Asha ─┘        └─► counts what's new since the last message, sends ONE notification
///
/// Guest.NotifyScheduledAt marks "a job is already waiting", so a burst of 200 uploads gives one message,
/// not 200. Guests who never subscribed to push and gave no email are skipped.
/// </summary>
public sealed class NotifyJobs(
    AppDbContext db,
    IPushSender push,
    IEmailSender email,
    IBackgroundJobClient jobs,
    IOptions<AppOptions> app,
    TimeProvider time,
    ILogger<NotifyJobs> log) : IGuestNotifier
{
    /// <summary>At most one message per guest per this interval.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    /// <summary>Wait a bit so a burst of uploads is collected into one message.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromMinutes(1);

    public async Task GuestsGotNewPhotosAsync(IReadOnlyCollection<Guid> guestIds, CancellationToken ct = default)
    {
        if (guestIds.Count == 0 || (!push.IsConfigured && !email.IsConfigured)) return;
        var now = time.GetUtcNow();
        var guests = await db.Guests
            .Where(g => guestIds.Contains(g.Id) && g.NotifyScheduledAt == null &&
                        (g.PushSubscriptionJson != null || (g.NotifyByEmail && g.Email != null)))
            .ToListAsync(ct);
        foreach (var guest in guests)
        {
            var when = now + BatchDelay;
            if (guest.LastNotifiedAt is { } last && last + MinInterval > when) when = last + MinInterval;
            guest.NotifyScheduledAt = when;
            jobs.Schedule<NotifyJobs>(j => j.SendAsync(guest.Id, false, CancellationToken.None), when);
        }
        await db.SaveChangesAsync(ct);
    }

    public void SendNow(Guid guestId) => jobs.Enqueue<NotifyJobs>(j => j.SendAsync(guestId, true, CancellationToken.None));

    /// <param name="force">Admin "resend": send even when nothing is new.</param>
    [Queue("default")]
    [AutomaticRetry(Attempts = 2)]
    public async Task SendAsync(Guid guestId, bool force, CancellationToken ct)
    {
        var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == guestId, ct);
        if (guest is null) return; // deleted meanwhile
        guest.NotifyScheduledAt = null;

        var link = await db.ShareLinks.AsNoTracking().Include(l => l.Event).FirstOrDefaultAsync(l => l.Id == guest.ShareLinkId, ct);
        var now = time.GetUtcNow();
        if (link?.Event is null || !link.IsUsable(now) || !link.Event.IsOpen(now))
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        // What's new since the guest last heard from us (or last looked: registration / Find more).
        var since = Max(guest.LastNotifiedAt, guest.LastSearchedAt) ?? guest.CreatedAt;
        var visible = db.GuestMatches.Where(m =>
            m.GuestId == guestId && (m.Status == MatchStatus.Auto || m.Status == MatchStatus.Confirmed) &&
            !m.Photo!.IsHidden && !m.Photo.Collection!.IsHidden);
        var fresh = await visible.CountAsync(m => m.CreatedAt > since, ct);
        var total = await visible.CountAsync(ct);
        if (fresh == 0 && !force)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var url = $"{app.Value.PublicBaseUrl.TrimEnd('/')}/g/{link.Token}";
        var (title, body) = Texts(link.Event.Name, fresh, total);
        var channels = new List<string>();

        if (guest.PushSubscriptionJson is { } sub && push.IsConfigured)
        {
            var result = await push.SendAsync(sub, new PushMessage(title, body, url), ct);
            if (result == PushResult.Gone) guest.PushSubscriptionJson = null; // phone unsubscribed
            if (result == PushResult.Sent) channels.Add("push");
        }
        if (guest is { NotifyByEmail: true, Email: { } to } && email.IsConfigured)
        {
            try
            {
                await email.SendAsync(to, title, $"{body}\n\nOpen your gallery: {url}\n", Html(link.Event.Name, title, body, url), ct);
                channels.Add("email");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Email to guest {GuestId} failed: {Error}", guestId, ex.Message);
            }
        }

        if (channels.Count > 0) guest.LastNotifiedAt = now;
        db.AuditLogs.Add(new AuditLog { Actor = "system", Action = "guest_notified", EntityId = guestId.ToString(), Details = $"{fresh} new via {string.Join("+", channels)}" });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Notified guest {GuestId}: {Fresh} new photo(s) via {Channels}", guestId, fresh, channels.Count > 0 ? string.Join("+", channels) : "nothing");
    }

    public static (string Title, string Body) Texts(string eventName, int fresh, int total) => fresh > 0
        ? ($"{fresh} new photo{(fresh == 1 ? "" : "s")} of you!", $"{eventName}: tap to see them ({total} in your gallery).")
        : ($"Your photos from {eventName}", $"{total} photo{(total == 1 ? "" : "s")} of you are waiting in your gallery.");

    private static string Html(string eventName, string title, string body, string url) =>
        $"""
        <div style="font-family:system-ui,sans-serif;max-width:480px;margin:auto;padding:24px;text-align:center">
          <p style="color:#888;margin:0">{WebUtility.HtmlEncode(eventName)}</p>
          <h1 style="font-size:22px">{WebUtility.HtmlEncode(title)}</h1>
          <p>{WebUtility.HtmlEncode(body)}</p>
          <p><a href="{WebUtility.HtmlEncode(url)}" style="display:inline-block;background:#b0004a;color:#fff;padding:12px 24px;border-radius:24px;text-decoration:none">Open my photos</a></p>
          <p style="color:#888;font-size:12px">You get this because you asked to be notified about new photos of you.</p>
        </div>
        """;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}

/// <summary>Used when no SignalR hub is wired up (e.g. a worker-only process).</summary>
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task PhotoProcessedAsync(Guid eventId, Guid photoId, PhotoStatus status, CancellationToken ct = default) => Task.CompletedTask;
    public Task NewMatchesAsync(Guid guestId, int count, CancellationToken ct = default) => Task.CompletedTask;
}
