using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Common;
using ShaadiLens.Infrastructure;
using ShaadiLens.Infrastructure.FaceService;
using ShaadiLens.Infrastructure.Notifications;
using ShaadiLens.Infrastructure.Storage;

namespace ShaadiLens.Api.Controllers;

public sealed record SmtpStatus(bool Configured, string? Host, int Port, string? From);
public sealed record PushStatus(bool Configured, string? PublicKey, string Subject);
public sealed record SettingsDto(
    string PublicBaseUrl, string StorageRoot, string FaceServiceUrl, int FaceWorkers, int ImageWorkers,
    SmtpStatus Smtp, PushStatus Push);
public sealed record TestEmailRequest(string To);
public sealed record VapidKeysResponse(string PublicKey, string PrivateKey, string EnvLines);

/// <summary>
/// Admin Settings: shows what is configured. Secrets are NOT stored in the database or editable here — they
/// live in .env (rule: "secrets only via .env"). This page only helps: test email, generate VAPID keys.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class SettingsController(
    IOptions<AppOptions> app, IOptions<StorageOptions> storage, IOptions<FaceServiceOptions> face,
    IOptions<ProcessingOptions> processing, IOptions<SmtpOptions> smtp, IOptions<PushOptions> push,
    IEmailSender email, IPushSender pushSender) : ControllerBase
{
    [HttpGet]
    public SettingsDto Get() => new(
        app.Value.PublicBaseUrl, storage.Value.Root, face.Value.BaseUrl,
        processing.Value.FaceWorkers, processing.Value.ImageWorkers,
        new SmtpStatus(email.IsConfigured, smtp.Value.Host, smtp.Value.Port, smtp.Value.From ?? smtp.Value.User),
        new PushStatus(pushSender.IsConfigured, pushSender.PublicKey, push.Value.Subject));

    /// <summary>Sends a test email so you know Gmail/SMTP settings work before the wedding.</summary>
    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail(TestEmailRequest req, CancellationToken ct)
    {
        if (!email.IsConfigured) throw new BadRequestException("SMTP isn't set up. Fill SMTP_HOST, SMTP_USER, SMTP_PASS, SMTP_FROM in .env and restart the api.");
        if (!MailAddress.TryCreate(req.To, out _)) throw new BadRequestException("Please enter a valid email address.");
        try
        {
            await email.SendAsync(req.To, "ShaadiLens test email",
                "If you can read this, email notifications work.", "<p>If you can read this, <b>email notifications work</b>.</p>", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Show the SMTP error (e.g. "535 Username and Password not accepted") — it's the admin's own server.
            throw new BadRequestException($"Sending failed: {ex.Message}");
        }
        return NoContent();
    }

    /// <summary>A new VAPID key pair. Not stored: copy the lines into .env and restart the api.</summary>
    [HttpPost("vapid-keys")]
    public VapidKeysResponse GenerateVapidKeys()
    {
        var (publicKey, privateKey) = WebPushSender.GenerateKeys();
        return new VapidKeysResponse(publicKey, privateKey, $"VAPID_PUBLIC={publicKey}\nVAPID_PRIVATE={privateKey}");
    }
}
