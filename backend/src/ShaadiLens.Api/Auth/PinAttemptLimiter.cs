using System.Collections.Concurrent;

namespace ShaadiLens.Api.Auth;

/// <summary>
/// Brute-force protection for link PINs: after 5 WRONG PINs from one IP for one link, that IP is blocked
/// for that link for 15 minutes.
///
/// Why count only failures (not every request)? At a wedding, hundreds of guests share the venue Wi-Fi
/// (= one public IP) and scan the same QR code. A plain request limiter would lock everyone out after
/// five guests. Counting wrong PINs still makes guessing hopeless: 5 guesses per 15 min against
/// 1,000,000 possible PINs.
/// </summary>
public sealed class PinAttemptLimiter(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public bool IsBlocked(string ip, string token, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_failures.TryGetValue(Key(ip, token), out var list)) return false;
        lock (list)
        {
            Prune(list);
            if (list.Count < MaxFailures) return false;
            retryAfter = list[0] + Window - time.GetUtcNow();
            return true;
        }
    }

    public void RecordFailure(string ip, string token)
    {
        var list = _failures.GetOrAdd(Key(ip, token), _ => []);
        lock (list)
        {
            Prune(list);
            list.Add(time.GetUtcNow());
        }
        // Keep memory bounded: occasionally drop empty entries.
        if (_failures.Count > 10_000)
            foreach (var (key, value) in _failures)
                lock (value) { Prune(value); if (value.Count == 0) _failures.TryRemove(key, out _); }
    }

    private void Prune(List<DateTimeOffset> list) => list.RemoveAll(t => t + Window <= time.GetUtcNow());

    private static string Key(string ip, string token) => $"{ip}|{token}";
}
