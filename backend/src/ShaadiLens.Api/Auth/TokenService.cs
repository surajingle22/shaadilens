using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ShaadiLens.Api.Auth;

/// <summary>
/// Issues JWTs (JSON Web Tokens): a signed JSON blob with "claims" (who you are, your role, expiry).
/// The server doesn't store sessions — it just verifies the signature on each request.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time)
{
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
        var token = Create(expires,
            new Claim(ClaimTypes.NameIdentifier, "admin"),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, AuthConstants.AdminRole));
        return (token, expires);
    }

    private string Create(DateTimeOffset expires, params Claim[] claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _opts.Issuer,
            Audience = _opts.Issuer,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = time.GetUtcNow().UtcDateTime,
            NotBefore = time.GetUtcNow().UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
