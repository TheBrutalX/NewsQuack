using System.Text.RegularExpressions;

namespace NewsQuack.App.Configuration;

/// <summary>
/// Masks Discord webhook URLs so the token (the last path segment — the secret) never appears in full in logs or error output.
/// A webhook URL looks like https://discord.com/api/webhooks/&lt;id&gt;/&lt;token&gt;; the masked form keeps scheme + host +
/// /api/webhooks/&lt;id&gt;/ and shows an ellipsis plus only the last 4 characters of the token (e.g. <c>…/AbCd</c>).
/// Pure function — no state, trivially testable. Non-webhook URLs (feed URLs, image URLs, relative paths) are not secrets and
/// pass through unchanged. Reusable by later tasks at their own log sites.
/// </summary>
public static class WebhookMasker
{
    /// <summary>
    /// Discord webhook shape. The scheme is optional and matching is case-insensitive on purpose: a malformed webhook URL that
    /// fails validation (wrong scheme, uppercase host) still gets masked at its echo sites instead of leaking the token.
    /// </summary>
    private static readonly Regex DiscordWebhookShape = new(
        @"^(?:(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*)://)?(?<host>(?:discord|discordapp)\.com)/api/webhooks/(?<id>[^/]+)/?(?<token>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Masks the token of a Discord webhook URL; returns non-webhook URLs (and null) unchanged.</summary>
    public static string? Mask(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        var match = DiscordWebhookShape.Match(url);
        if (!match.Success)   // not a Discord webhook shape — feed URL, image URL, relative path… print as-is
            return url;

        var token = match.Groups["token"].Value;
        var visibleTail = token.Length <= 4 ? token : token[^4..];

        var scheme = match.Groups["scheme"];
        return $"{(scheme.Success ? $"{scheme.Value}://" : string.Empty)}{match.Groups["host"].Value}/api/webhooks/{match.Groups["id"].Value}/…{visibleTail}";
    }
}
