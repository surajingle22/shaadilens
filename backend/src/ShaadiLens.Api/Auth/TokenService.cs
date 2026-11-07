using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ShaadiLens.Application.Guests;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Api.Auth;

/// <summary>
/// Issues JWTs (JSON Web Tokens): a signed JSON blob with "claims" (who you are, your role, expiry).
/// The server doesn't store sessions — it just verifies the signature on each request.
///
///   admin token   role=Admin, 7 days, in an HttpOnly cookie
///   guest token   role=Guest + event/link/guest ids, 30 days, sent as Bearer
///   tickets       short-lived, single-purpose (e.g. "PIN was entered", "may download this ZIP").
///                 They use a different audience, so a ticket can never be used as a login token.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    public const string EventIdClaim = "eid";
    public const string LinkIdClaim = "lid";
    public const string GuestIdClaim = "gid";
    public const string LinkTypeClaim = "ltype";

    private readonly JwtOptions _opts = options.Value;

    public SymmetricSecurityKey SigningKey => CreateKey(_opts.Secret);

    public static SymmetricSecurityKey CreateKey(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret (JWT_SECRET) must be at least 32 characters");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateAdminToken(string email)
    {
        var expires = time.GetUtcNow().AddDays(_opts.AdminTokenDays);
        var token = Create(_opts.Issuer, expires,
            new Claim(ClaimTypes.NameIdentifier, "admin"),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, AuthConstants.AdminRole));
        return (token, expires);
    }

    /// <summary>Guest JWT scoped to one event + one link (+ one guest, unless anonymous Full-gallery viewer).</summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateGuestToken(GuestContext ctx)
    {
        var expires = time.GetUtcNow().AddDays(_opts.GuestTokenDays);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ctx.GuestId?.ToString() ?? $"link:{ctx.LinkId}"),
            new(ClaimTypes.Role, AuthConstants.GuestRole),
            new(EventIdClaim, ctx.EventId.ToString()),
            new(LinkIdClaim, ctx.LinkId.ToString()),
            new(LinkTypeClaim, ctx.LinkType.ToString()),
        };
        if (ctx.GuestId is { } gid) claims.Add(new Claim(GuestIdClaim, gid.ToString()));
        return (Create(_opts.Issuer, expires, [.. claims]), expires);
    }

    public static GuestContext GuestContextFrom(ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(EventIdClaim)!),
        Guid.Parse(user.FindFirstValue(LinkIdClaim)!),
        Guid.TryParse(user.FindFirstValue(GuestIdClaim), out var g) ? g : null,
        Enum.Parse<ShareLinkType>(user.FindFirstValue(LinkTypeClaim)!));

    public string CreateTicket(string purpose, TimeSpan lifetime, IDictionary<string, string> values)
    {
        var claims = values.Select(kv => new Claim(kv.Key, kv.Value)).ToArray();
        return Create(TicketAudience(purpose), time.GetUtcNow() + lifetime, claims);
    }

    /// <summary>Returns the ticket's claims, or null if it's invalid, expired or for another purpose.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> ReadTicketAsync(string purpose, string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(ticket, new TokenValidationParameters
        {
            ValidIssuer = _opts.Issuer,
            ValidAudience = TicketAudience(purpose),
            IssuerSigningKey = SigningKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        return result.IsValid
            ? result.Claims.ToDictionary(c => c.Key, c => c.Value?.ToString() ?? "")
            : null;
    }

    private string TicketAudience(string purpose) => $"{_opts.Issuer}:ticket:{purpose}";

    private string Create(string audience, DateTimeOffset expires, params Claim[] claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _opts.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = time.GetUtcNow().UtcDateTime,
            NotBefore = time.GetUtcNow().UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
