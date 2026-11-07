namespace ShaadiLens.Api.Auth;

/// <summary>The single admin account, from .env (ADMIN_EMAIL / ADMIN_PASSWORD).</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    /// <summary>HMAC key for signing tokens (env JWT_SECRET, ≥ 32 chars).</summary>
    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "shaadilens";
    public int AdminTokenDays { get; set; } = 7;
    public int GuestTokenDays { get; set; } = 30;
}

public static class AuthConstants
{
    public const string AdminRole = "Admin";
    public const string GuestRole = "Guest";
    public const string AdminPolicy = "Admin";
    public const string GuestPolicy = "Guest";
    /// <summary>HttpOnly cookie holding the admin JWT (JavaScript can't read it → safe from XSS token theft).</summary>
    public const string AdminCookie = "sl_admin";
}
