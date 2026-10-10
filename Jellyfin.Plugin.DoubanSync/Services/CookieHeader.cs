using System.Text;

namespace Jellyfin.Plugin.DoubanSync.Services;

/// <summary>
/// Parses and normalizes a browser Cookie request header.
/// </summary>
internal static class CookieHeader
{
    private const int MaximumCookieLength = 16 * 1024;

    /// <summary>
    /// Validates and normalizes a Cookie header copied from a browser.
    /// </summary>
    /// <param name="rawCookie">Raw browser Cookie header.</param>
    /// <returns>A normalized Cookie header.</returns>
    public static string Normalize(string rawCookie)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCookie);
        if (rawCookie.Length > MaximumCookieLength)
        {
            throw new ArgumentException("Cookie 太长，最大允许 16 KiB。", nameof(rawCookie));
        }

        if (rawCookie.Contains('\r', StringComparison.Ordinal)
            || rawCookie.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Cookie 中不能包含换行符。", nameof(rawCookie));
        }

        var parsed = Parse(rawCookie);
        if (!parsed.ContainsKey("dbcl2"))
        {
            throw new ArgumentException("Cookie 缺少 dbcl2，请确认复制自已登录的 douban.com 页面。", nameof(rawCookie));
        }

        if (!parsed.ContainsKey("ck"))
        {
            throw new ArgumentException("Cookie 缺少 ck，无法提交“看过”状态。", nameof(rawCookie));
        }

        var builder = new StringBuilder(rawCookie.Length);
        foreach (var pair in parsed)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(pair.Key).Append('=').Append(pair.Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gets the CSRF token from a normalized Cookie header.
    /// </summary>
    /// <param name="cookie">Cookie header.</param>
    /// <returns>Douban CSRF token.</returns>
    public static string GetCsrfToken(string cookie)
    {
        var parsed = Parse(cookie);
        if (!parsed.TryGetValue("ck", out var token) || string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Cookie 缺少有效的 ck。", nameof(cookie));
        }

        return token.Trim('"');
    }

    /// <summary>
    /// Applies rotated values from Set-Cookie response headers to an imported Cookie header.
    /// Only cookies that were already present are updated, so unrelated response cookies are not accumulated.
    /// </summary>
    /// <param name="cookie">Normalized browser Cookie header.</param>
    /// <param name="setCookieHeaders">Set-Cookie response header values.</param>
    /// <returns>The normalized Cookie header with rotated values applied.</returns>
    internal static string MergeResponseCookies(
        string cookie,
        IEnumerable<string> setCookieHeaders)
    {
        var parsed = Parse(Normalize(cookie));
        foreach (var header in setCookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var pairEnd = header.IndexOf(';', StringComparison.Ordinal);
            var pair = pairEnd >= 0 ? header[..pairEnd] : header;
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            if (!parsed.ContainsKey(name) || !IsValidCookieName(name))
            {
                continue;
            }

            var value = pair[(separator + 1)..].Trim();
            var attributes = pairEnd >= 0 ? header[(pairEnd + 1)..] : string.Empty;
            var expired = value.Length == 0
                || attributes.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(attribute => string.Equals(attribute, "Max-Age=0", StringComparison.OrdinalIgnoreCase));
            if (expired)
            {
                parsed.Remove(name);
            }
            else
            {
                parsed[name] = value;
            }
        }

        return Normalize(string.Join("; ", parsed.Select(value => $"{value.Key}={value.Value}")));
    }

    private static Dictionary<string, string> Parse(string rawCookie)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in rawCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (name.Length == 0 || !IsValidCookieName(name))
            {
                continue;
            }

            result[name] = value;
        }

        return result;
    }

    private static bool IsValidCookieName(string name)
    {
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character)
                || character is '!' or '#' or '$' or '%' or '&' or '\''
                    or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
