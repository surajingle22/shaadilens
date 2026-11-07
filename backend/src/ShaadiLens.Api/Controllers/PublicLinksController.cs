using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Application.Guests;

namespace ShaadiLens.Api.Controllers;

public sealed record VerifyPinRequest(string Pin);
public sealed record PinTicketResponse(string PinTicket);

public sealed class RegisterForm
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool Consent { get; set; }
    /// <summary>From verify-pin, when the link has a PIN.</summary>
    public string? PinTicket { get; set; }
    public IFormFile? Selfie { get; set; }
}

public sealed record GuestLoginResponse(string Token, DateTimeOffset ExpiresAt, GuestSessionDto Session, MatchSummary? Matches);

/// <summary>
/// The first steps a guest takes after scanning the QR: landing info → PIN → register (+ selfie).
/// No login yet; the result of register is the guest JWT.
/// </summary>
[ApiController]
[Route("api/v1/public/links/{token}")]
public sealed class PublicLinksController(GuestAccessService guests, TokenService tokens, PinAttemptLimiter pinLimiter) : ControllerBase
{
    private const string PinTicketPurpose = "pin";

    [HttpGet]
    public Task<PublicLinkDto> Get(string token, CancellationToken ct) => guests.GetLinkAsync(token, ct);

    [HttpPost("verify-pin")]
    public async Task<ActionResult<PinTicketResponse>> VerifyPin(string token, VerifyPinRequest req, CancellationToken ct)
    {
        var ip = AuthSetup.Ip(HttpContext);
        if (pinLimiter.IsBlocked(ip, token, out var retryAfter))
        {
            Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            return Problem($"Too many wrong PINs. Try again in {Math.Ceiling(retryAfter.TotalMinutes)} minutes.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!await guests.VerifyPinAsync(token, req.Pin ?? "", ip, ct))
        {
            pinLimiter.RecordFailure(ip, token);
            return Problem("That PIN isn't right. Check the card or ask the couple.", statusCode: StatusCodes.Status401Unauthorized);
        }

        // A short-lived ticket proving the PIN was entered, valid only for this link.
        var ticket = tokens.CreateTicket(PinTicketPurpose, TimeSpan.FromHours(2), new Dictionary<string, string> { ["token"] = token });
        return new PinTicketResponse(ticket);
    }

    /// <summary>multipart/form-data: name, phone, email, consent, pinTicket, selfie (image file).</summary>
    [HttpPost("register")]
    [EnableRateLimiting(AuthSetup.RegisterRateLimit)]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<GuestLoginResponse> Register(string token, [FromForm] RegisterForm form, CancellationToken ct)
    {
        var ticket = await tokens.ReadTicketAsync(PinTicketPurpose, form.PinTicket);
        var pinVerified = ticket is not null && ticket.GetValueOrDefault("token") == token;

        await using var selfie = form.Selfie?.OpenReadStream();
        var (context, matches) = await guests.RegisterAsync(
            token, new RegisterRequest(form.Name, form.Phone, form.Email, form.Consent), selfie, pinVerified,
            AuthSetup.Ip(HttpContext), ct);

        var (jwt, expires) = tokens.CreateGuestToken(context);
        return new GuestLoginResponse(jwt, expires, await guests.GetSessionAsync(context, ct), matches);
    }
}
