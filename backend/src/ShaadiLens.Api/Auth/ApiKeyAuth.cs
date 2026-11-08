using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ShaadiLens.Api.Auth;

/// <summary>UPLOADER_API_KEY from .env. Empty = the hot-folder uploader is switched off.</summary>
public sealed class UploaderOptions
{
    public const string Section = "Uploader";
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Lets the hot-folder uploader (tools/HotFolderUploader) authenticate with "X-Api-Key: …". The key grants
/// the Uploader role only: upload photos and list events/collections — no guests, no settings, no deletes.
/// </summary>
public sealed class ApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<UploaderOptions> uploader)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-Api-Key";
    /// <summary>Short keys are refused so nobody runs with "1234".</summary>
    public const int MinKeyLength = 24;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var given) || string.IsNullOrEmpty(given)) return Task.FromResult(AuthenticateResult.NoResult());
        var expected = uploader.Value.ApiKey;
        if (expected.Length < MinKeyLength) return Task.FromResult(AuthenticateResult.Fail("Uploader API key is not configured"));
        // Constant-time comparison: the time taken doesn't reveal how many characters were right.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given.ToString()), Encoding.UTF8.GetBytes(expected)))
            return Task.FromResult(AuthenticateResult.Fail("Wrong API key"));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "hot-folder-uploader"), new Claim(ClaimTypes.Role, AuthConstants.UploaderRole)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
