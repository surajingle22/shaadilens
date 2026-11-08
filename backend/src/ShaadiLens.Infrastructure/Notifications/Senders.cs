using System.Net;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using ShaadiLens.Application.Abstractions;
using WebPush;

namespace ShaadiLens.Infrastructure.Notifications;

/// <summary>SMTP settings from .env (SMTP_HOST/PORT/USER/PASS/FROM). Gmail: smtp.gmail.com:587 + an app password.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Pass { get; set; }
    public string? From { get; set; }
}

/// <summary>
/// Web Push "VAPID" keys from .env (VAPID_PUBLIC/PRIVATE/SUBJECT). VAPID = the server proves to the browser's
/// push service (Google/Mozilla/Apple, all free) that messages really come from us. Generate a pair in
/// admin Settings.
/// </summary>
public sealed class PushOptions
{
    public const string Section = "Push";
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }
    public string Subject { get; set; } = "mailto:admin@example.com";
}

/// <summary>Email via MailKit (the recommended .NET SMTP library; System.Net.Mail.SmtpClient is obsolete).</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    private readonly SmtpOptions _o = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.Host) && !string.IsNullOrWhiteSpace(_o.From ?? _o.User);

    public async Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("SMTP is not configured (SMTP_HOST / SMTP_FROM in .env).");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_o.From ?? _o.User!));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody }.ToMessageBody();

        using var smtp = new SmtpClient { Timeout = 20_000 };
        // 465 = TLS from the start; 587/25 = STARTTLS when the server offers it.
        await smtp.ConnectAsync(_o.Host!, _o.Port, _o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
        if (!string.IsNullOrEmpty(_o.User)) await smtp.AuthenticateAsync(_o.User, _o.Pass ?? "", ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
        log.LogInformation("Email sent via {Host}", _o.Host); // never log the address
    }
}

/// <summary>
/// Web Push with the `WebPush` NuGet: encrypts the message for the browser's keys and posts it to the
/// browser's push service, signed with our VAPID key. The phone shows it even when the page is closed.
/// </summary>
public sealed class WebPushSender(IOptions<PushOptions> options, IHttpClientFactory http, ILogger<WebPushSender> log) : IPushSender
{
    private readonly PushOptions _o = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.PublicKey) && !string.IsNullOrWhiteSpace(_o.PrivateKey);
    public string? PublicKey => IsConfigured ? _o.PublicKey : null;

    public async Task<PushResult> SendAsync(string subscriptionJson, PushMessage message, CancellationToken ct = default)
    {
        if (!IsConfigured) return PushResult.Failed;
        var sub = ParseSubscription(subscriptionJson);
        if (sub is null) return PushResult.Gone;

        // Payload format understood by Angular's service worker (ngsw): it shows the notification and,
        // on tap, opens/focuses the gallery URL.
        var payload = JsonSerializer.Serialize(new
        {
            notification = new
            {
                title = message.Title,
                body = message.Body,
                icon = "/icons/icon-192.png",
                badge = "/icons/icon-72.png",
                data = new { onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url = message.Url } } },
            },
        });

        try
        {
            using var client = new WebPushClient(http.CreateClient(nameof(WebPushSender)));
            await client.SendNotificationAsync(sub, payload, new VapidDetails(_o.Subject, _o.PublicKey, _o.PrivateKey), ct);
            return PushResult.Sent;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return PushResult.Gone;
        }
        catch (Exception ex) when (ex is WebPushException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Web push failed: {Error}", ex.Message);
            return PushResult.Failed;
        }
    }

    /// <summary>Browser subscription JSON: { endpoint, keys: { p256dh, auth } }. Null if malformed.</summary>
    public static PushSubscription? ParseSubscription(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var endpoint = root.GetProperty("endpoint").GetString();
            var keys = root.GetProperty("keys");
            var p256dh = keys.GetProperty("p256dh").GetString();
            var auth = keys.GetProperty("auth").GetString();
            if (string.IsNullOrEmpty(endpoint) || !endpoint.StartsWith("https://", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(p256dh) || string.IsNullOrEmpty(auth))
                return null;
            return new PushSubscription(endpoint, p256dh, auth);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A fresh VAPID key pair for the admin to paste into .env.</summary>
    public static (string PublicKey, string PrivateKey) GenerateKeys()
    {
        var keys = VapidHelper.GenerateVapidKeys();
        return (keys.PublicKey, keys.PrivateKey);
    }
}
