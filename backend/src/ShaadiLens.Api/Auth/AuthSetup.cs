using System.Security.Claims;
using System.Threading.RateLimiting;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ShaadiLens.Api.Auth;

public static class AuthSetup
{
    public const string LoginRateLimit = "login";

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
                        if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization) &&
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

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // 10 login attempts per 15 minutes per IP address.
            o.AddPolicy(LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));
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
