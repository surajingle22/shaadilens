using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Application.Abstractions;

/// <summary>
/// Live updates to open browsers (SignalR, implemented in the Api project). Fire-and-forget: a failure here
/// must never fail a photo job.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>A photo finished a pipeline step → admin dashboards update, Full-gallery viewers see "new photos".</summary>
    Task PhotoProcessedAsync(Guid eventId, Guid photoId, PhotoStatus status, CancellationToken ct = default);

    /// <summary>"3 new photos of you" banner for a guest who has the gallery open.</summary>
    Task NewMatchesAsync(Guid guestId, int count, CancellationToken ct = default);

    /// <summary>The guest's highlight reel changed state (rendering / ready / failed).</summary>
    Task ReelUpdatedAsync(Guid guestId, Guid reelId, Domain.Entities.ReelStatus status, CancellationToken ct = default);
}

/// <summary>
/// Web Push + email for guests whose gallery is NOT open. Debounced: at most one message per guest per
/// 10 minutes ("12 new photos of you"), sent by a background job.
/// </summary>
public interface IGuestNotifier
{
    /// <summary>These guests just got new photos (reverse matching). Schedules a debounced notification.</summary>
    Task GuestsGotNewPhotosAsync(IReadOnlyCollection<Guid> guestIds, CancellationToken ct = default);

    /// <summary>Admin "resend": send now, even if nothing is new.</summary>
    void SendNow(Guid guestId);
}

public sealed record PushMessage(string Title, string Body, string Url);

public enum PushResult
{
    Sent,
    /// <summary>The browser unsubscribed (404/410): forget the subscription.</summary>
    Gone,
    Failed,
}

public interface IPushSender
{
    /// <summary>VAPID keys are set in .env.</summary>
    bool IsConfigured { get; }
    /// <summary>The VAPID public key browsers need to subscribe (null when not configured).</summary>
    string? PublicKey { get; }
    Task<PushResult> SendAsync(string subscriptionJson, PushMessage message, CancellationToken ct = default);
}

public interface IEmailSender
{
    /// <summary>SMTP host + from-address are set in .env.</summary>
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default);
}
