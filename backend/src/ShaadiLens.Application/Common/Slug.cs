using System.Text;

namespace ShaadiLens.Application.Common;

public static class Slug
{
    /// <summary>"Suraj & Priya's Wedding!" → "suraj-priya-s-wedding".</summary>
    public static string From(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return slug.Length == 0 ? "event" : slug;
    }
}
