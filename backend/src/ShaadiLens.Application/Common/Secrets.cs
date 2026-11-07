using System.Security.Cryptography;

namespace ShaadiLens.Application.Common;

/// <summary>Random tokens and PIN hashing.</summary>
public static class Secrets
{
    private const string UrlSafe = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/l/I look-alikes

    /// <summary>32 chars from a 57-char alphabet ≈ 186 bits of randomness: impossible to guess.</summary>
    public static string NewLinkToken() => RandomNumberGenerator.GetString(UrlSafe, 32);

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private const int Iterations = 100_000;

    /// <summary>
    /// PBKDF2-SHA256 with a random salt, stored as "pbkdf2$iterations$salt$hash".
    /// Slow on purpose: even with the database, trying all 1,000,000 PINs takes a long time.
    /// </summary>
    public static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPin(string pin, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class AppOptions
{
    public const string Section = "App";
    /// <summary>Public URL guests use (Cloudflare Tunnel hostname). Used in QR codes and links.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
}
