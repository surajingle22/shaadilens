using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ShaadiLens.Application.Abstractions;
using ShaadiLens.Domain.Enums;

namespace ShaadiLens.Infrastructure.Media;

public sealed class MediaOptions
{
    public const string Section = "Media";
    /// <summary>HMAC key (env MEDIA_SIGNING_KEY). Keep secret: anyone with it can mint photo URLs.</summary>
    public string SigningKey { get; set; } = "";
    public int UrlLifetimeMinutes { get; set; } = 60;
}

/// <summary>
/// Signed media URLs: /media/{photoId}/{variant}?exp={unix}&amp;sig={hmac}
///
/// The API only hands out a URL after checking the caller may see that photo (admin, or a guest
/// matched in it). The HMAC signature proves the URL came from us and hasn't been edited (e.g.
/// swapping the photoId); `exp` makes leaked URLs stop working.
///
/// Expiry is rounded UP to the next whole hour, so all URLs issued within the same hour are
/// identical → the browser cache hits instead of re-downloading thumbnails on every page load.
/// </summary>
public sealed class MediaUrlSigner : IMediaUrlSigner
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;

    public MediaUrlSigner(IOptions<MediaOptions> options, TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(options.Value.SigningKey) || options.Value.SigningKey.Length < 32)
            throw new InvalidOperationException("Media:SigningKey (MEDIA_SIGNING_KEY) must be at least 32 characters");
        _key = Encoding.UTF8.GetBytes(options.Value.SigningKey);
        _lifetime = TimeSpan.FromMinutes(options.Value.UrlLifetimeMinutes);
        _time = time;
    }

    public string GetUrl(Guid photoId, PhotoVariant variant)
    {
        var expiresAt = _time.GetUtcNow() + _lifetime;
        var exp = (long)Math.Ceiling(expiresAt.ToUnixTimeSeconds() / 3600.0) * 3600;
        return $"/media/{photoId}/{variant.ToString().ToLowerInvariant()}?exp={exp}&sig={Sign(photoId, variant, exp)}";
    }

    public bool Verify(Guid photoId, PhotoVariant variant, long expires, string signature, DateTimeOffset now)
    {
        if (expires < now.ToUnixTimeSeconds()) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(photoId, variant, expires));
        var actual = Encoding.ASCII.GetBytes(signature);
        // Constant-time compare: doesn't leak how many characters matched.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Sign(Guid photoId, PhotoVariant variant, long exp)
    {
        var payload = Encoding.UTF8.GetBytes($"{photoId:N}|{(int)variant}|{exp}");
        var hash = HMACSHA256.HashData(_key, payload);
        return Base64UrlEncode(hash.AsSpan(0, 16)); // 128 bits is plenty for a URL signature
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
