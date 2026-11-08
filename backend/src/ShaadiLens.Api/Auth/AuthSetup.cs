using System.Security.Claims;
using System.Threading.RateLimiting;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ShaadiLens.Api.Auth;

public static class AuthSetup
{
    public const string LoginRateLimit = "login";
    public const string RegisterRateLimit = "register";
    public const string RefreshRateLimit = "refresh";

    public static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IServiceCollection AddAppAuth(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.AddSingleton<TokenService>();

        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; // keep claim names as issued
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Issuer,
                    IssuerSigningKey = TokenService.CreateKey(jwt.Secret),
                    NameClaimType = ClaimTypes.NameIdentifier,
                    RoleClaimType = ClaimTypes.Role,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                o.Events = new JwtBearerEvents
                {
                    // Guests send "Authorization: Bearer …". The admin's browser sends the HttpOnly cookie
                    // instead — that's what makes /hangfire and plain <a href> downloads work for the admin.
                    OnMessageReceived = ctx =>
                    {
                        // Browsers can't add headers to a WebSocket, so the SignalR client sends the guest
                        // token as ?access_token=… — accepted ONLY on the hub path.
                        if (ctx.Request.Path.StartsWithSegments("/hubs") &&
                            ctx.Request.Query.TryGetValue("access_token", out var hubToken) && !string.IsNullOrEmpty(hubToken))
                        {
                            ctx.Token = hubToken;
                        }
                        else if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization) &&
                            ctx.Request.Cookies.TryGetValue(AuthConstants.AdminCookie, out var cookie))
                        {
                            ctx.Token = cookie;
                        }
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.AdminPolicy, p => p.RequireRole(AuthConstants.AdminRole))
            .AddPolicy(AuthConstants.GuestPolicy, p => p.RequireRole(AuthConstants.GuestRole));

        services.AddSingleton<PinAttemptLimiter>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // 10 admin login attempts per 15 minutes per IP address.
            o.AddPolicy(LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                Ip(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));
            // Selfie registration is CPU-heavy (face analysis). Generous per IP because a whole wedding
            // can share one venue Wi-Fi IP, but enough to stop a script hammering face-service.
            o.AddPolicy(RegisterRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                Ip(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            // "Check for new photos": per guest.
            o.AddPolicy(RefreshRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Ip(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }
}

/// <summary>Only the logged-in admin may open the Hangfire dashboard at /hangfire.</summary>
public sealed class HangfireAdminAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().User.IsInRole(AuthConstants.AdminRole);
}
