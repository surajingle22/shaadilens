using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Api.Realtime;

/// <summary>
/// SignalR hub at /hubs/events — a WebSocket the browser keeps open so the server can push updates.
///
/// Groups (a group = a named set of connections we can broadcast to):
///   admin:{eventId}     the admin's dashboard/photos page for that event  ← PhotoProcessed
///   viewers:{eventId}   Full/Selection-gallery viewers and the TV slideshow ← PhotoProcessed (when ready)
///   guest:{guestId}     one guest's phone(s)                              ← NewMatchesForYou
///
/// Guests are put in their groups automatically from their JWT (sent as ?access_token=…, because browsers
/// can't set headers on WebSockets). The admin asks to watch an event with WatchEvent.
/// </summary>
[Authorize]
public sealed class EventsHub : Hub
{
    public const string Path = "/hubs/events";

    public static string AdminGroup(Guid eventId) => $"admin:{eventId}";
    public static string ViewersGroup(Guid eventId) => $"viewers:{eventId}";
    public static string GuestGroup(Guid guestId) => $"guest:{guestId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.IsInRole(AuthConstants.GuestRole) == true)
        {
            var ctx = TokenService.GuestContextFrom(Context.User);
            if (ctx.GuestId is { } guestId) await Groups.AddToGroupAsync(Context.ConnectionId, GuestGroup(guestId));
            if (ctx.LinkType is ShareLinkType.Full or ShareLinkType.Selection)
                await Groups.AddToGroupAsync(Context.ConnectionId, ViewersGroup(ctx.EventId));
        }
        await base.OnConnectedAsync();
    }

    [Authorize(Policy = AuthConstants.AdminPolicy)]
    public Task WatchEvent(Guid eventId) => Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup(eventId));

    [Authorize(Policy = AuthConstants.AdminPolicy)]
    public Task UnwatchEvent(Guid eventId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminGroup(eventId));
}

/// <summary>Sends hub messages from background jobs (Hangfire runs in the same process).</summary>
public sealed class SignalRRealtimeNotifier(IHubContext<EventsHub> hub, ILogger<SignalRRealtimeNotifier> log) : IRealtimeNotifier
{
    public async Task PhotoProcessedAsync(Guid eventId, Guid photoId, PhotoStatus status, CancellationToken ct = default)
    {
        await SafeAsync(() => hub.Clients.Group(EventsHub.AdminGroup(eventId)).SendAsync("PhotoProcessed", new { eventId, photoId, status }, ct));
        // Viewers only care once a photo is fully done (it then appears in the gallery).
        if (status is PhotoStatus.Matched)
            await SafeAsync(() => hub.Clients.Group(EventsHub.ViewersGroup(eventId)).SendAsync("PhotoAdded", new { photoId }, ct));
    }

    public Task NewMatchesAsync(Guid guestId, int count, CancellationToken ct = default) =>
        SafeAsync(() => hub.Clients.Group(EventsHub.GuestGroup(guestId)).SendAsync("NewMatchesForYou", new { count }, ct));

    public Task ReelUpdatedAsync(Guid guestId, Guid reelId, Domain.Entities.ReelStatus status, CancellationToken ct = default) =>
        SafeAsync(() => hub.Clients.Group(EventsHub.GuestGroup(guestId)).SendAsync("ReelUpdated", new { reelId, status }, ct));

    /// <summary>Live updates are a nice-to-have: never let them fail a photo job.</summary>
    private async Task SafeAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "SignalR send failed");
        }
    }
}
