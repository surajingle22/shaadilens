using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ShaadiLens.Api.Auth;

namespace ShaadiLens.Api.Controllers;

public sealed record LoginRequest(string Email, string Password);
public sealed record AdminSessionResponse(string Email, DateTimeOffset? ExpiresAt);

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IOptions<AdminOptions> admin, TokenService tokens, ILogger<AuthController> log) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting(AuthSetup.LoginRateLimit)]
    public ActionResult<AdminSessionResponse> Login(LoginRequest req)
    {
        var a = admin.Value;
        if (string.IsNullOrEmpty(a.Email) || string.IsNullOrEmpty(a.Password))
            return Problem("Admin account is not configured (ADMIN_EMAIL / ADMIN_PASSWORD).", statusCode: 500);

        var ok = req.Email.Trim().Equals(a.Email, StringComparison.OrdinalIgnoreCase) & SecureEquals(req.Password, a.Password);
        if (!ok)
        {
            log.LogWarning("Failed admin login from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Problem("Wrong email or password.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var (token, expires) = tokens.CreateAdminToken(a.Email);
        Response.Cookies.Append(AuthConstants.AdminCookie, token, new CookieOptions
        {
            HttpOnly = true,                      // invisible to JavaScript
            Secure = Request.IsHttps,             // HTTPS-only when served via Cloudflare Tunnel
            SameSite = SameSiteMode.Strict,       // not sent on cross-site requests → CSRF protection
            Expires = expires,
            Path = "/",
        });
        return new AdminSessionResponse(a.Email, expires);
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(AuthConstants.AdminCookie, new CookieOptions { Path = "/" });
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    public AdminSessionResponse Me() => new(User.FindFirstValue(ClaimTypes.Email) ?? "", null);

    /// <summary>Compare hashes in constant time so response timing doesn't reveal the password.</summary>
    private static bool SecureEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
