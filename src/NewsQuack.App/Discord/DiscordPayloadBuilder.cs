using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NewsQuack.App.Configuration;
using NewsQuack.App.Parsing;   // shared HtmlPlainText helper (T-013) — PlainText delegates to it
using NewsQuack.Core;

namespace NewsQuack.App.Discord;

/// <summary>
/// Pure payload construction for the three Discord webhook modes (T-020) — no HTTP, no I/O, trivially testable.
/// Every Discord-specific limit is applied here so an over-limit field can never reach the wire: truncation always
/// ends in a single trailing ellipsis and yields exactly <c>limit</c> characters.
/// </summary>
public static class DiscordPayloadBuilder
{
    public const int EmbedTitleLimit = 256;
    public const int EmbedDescriptionLimit = 4096;
    public const int ThreadNameLimit = 100;

    // Author fields are truncated too — "never emit an over-limit field" applies to every field in every mode.
    public const int AuthorNameLimit = 512;
    public const int AuthorUrlLimit = 1024;

    // T-052: Discord's embed footer text limit — the feed-source footer must never exceed it or Discord rejects the whole embed.
    public const int FooterTextLimit = 2048;

    /// <summary>Maps a config "Mode" value to its enum, case-insensitively. Unknown values fall back to embed — the schema
    /// ([EnumLike]) already rejects anything outside embed|forum|markdown at validation time, so this is belt-and-braces.</summary>
    public static DiscordMode ParseMode(string? mode) => (mode ?? "embed").Trim().ToLowerInvariant() switch
    {
        "forum" => DiscordMode.Forum,
        "markdown" => DiscordMode.Markdown,
        _ => DiscordMode.Embed,
    };

    /// <summary>Builds the JSON body for one webhook POST in the given mode. The mode is explicit (not read from config) so the
    /// one-shot channel-type swap can resend the same post in a different mode without a second config object.</summary>
    public static string BuildPayload(Post post, DiscordTargetConfig config, DiscordMode mode) => mode switch
    {
        DiscordMode.Forum => BuildForumPayload(post, config),
        DiscordMode.Markdown => BuildMarkdownPayload(post, config),
        _ => BuildEmbedPayload(post, config),   // Embed — the default
    };

    private static string BuildEmbedPayload(Post post, DiscordTargetConfig config)
    {
        var payload = new JsonObject();
        payload["embeds"] = new JsonArray(BuildEmbedObject(post, config));
        return payload.ToJsonString();
    }

    private static string BuildForumPayload(Post post, DiscordTargetConfig config)
    {
        // Same embed as embed mode, plus "thread_name" at the top level.
        var payload = new JsonObject();
        payload["embeds"] = new JsonArray(BuildEmbedObject(post, config));
        payload["thread_name"] = TruncateWithEllipsis(ThreadNameFor(post), ThreadNameLimit);
        return payload.ToJsonString();
    }

    private static string BuildMarkdownPayload(Post post, DiscordTargetConfig config)
    {
        // Amended template (T-020 frozen, T-052 amendment, T-053 refinement): **<title>**\n<link>\n\n<plain-text description>\n\n<effective image url>\n\nFeed: <sourceName or sourceUrl> —
        // each previously-optional part is still omitted entirely when absent; the feed section is always present (the label is the feed's display
        // name when non-blank, else the required Post.SourceUrl). PlainText preserves line structure but normalises it (per-line collapse + trim, at most one blank
        // line internally), so the assembled string still never contains 3+ consecutive newlines. Title and description are truncated to their embed limits
        // before assembly so no part ever exceeds its limit in any mode.
        var markdown = new StringBuilder();

        markdown.Append("**").Append(TruncateWithEllipsis(post.Title, EmbedTitleLimit)).Append("**");

        if (!string.IsNullOrEmpty(post.Link))
            markdown.Append('\n').Append(post.Link);

        var description = PlainText(post.Description);
        if (description is not null)
            markdown.Append("\n\n").Append(TruncateWithEllipsis(description, EmbedDescriptionLimit));

        var image = EffectiveImage(post, config);
        if (image is not null)
            markdown.Append("\n\n").Append(image);

        // T-052: final feed-source section — an explicit decision amending the frozen T-020 template (recorded in tasks/t-052);
        // T-053: label is the feed's display name when known, else the URL.
        markdown.Append("\n\n").Append(TruncateWithEllipsis("Feed: " + FeedLabel(post), FooterTextLimit));

        var payload = new JsonObject();
        payload["content"] = markdown.ToString();
        return payload.ToJsonString();
    }

    /// <summary>The single embed object shared by embed and forum modes — built once so both stay field-for-field identical.</summary>
    private static JsonObject BuildEmbedObject(Post post, DiscordTargetConfig config)
    {
        var embed = new JsonObject();

        embed["title"] = TruncateWithEllipsis(post.Title, EmbedTitleLimit);

        var description = PlainText(post.Description);   // null when absent or empty after stripping → field omitted
        if (description is not null)
            embed["description"] = TruncateWithEllipsis(description, EmbedDescriptionLimit);

        if (!string.IsNullOrEmpty(post.Link))
            embed["url"] = post.Link;                    // omitted when null

        if (config.Color is { } color)
            embed["color"] = color;                      // omitted when null

        if (post.Author is { } author)                   // omitted when post.Author is null
        {
            var authorObject = new JsonObject();
            authorObject["name"] = TruncateWithEllipsis(author.Name, AuthorNameLimit);
            if (!string.IsNullOrEmpty(author.Url))
                authorObject["url"] = TruncateWithEllipsis(author.Url, AuthorUrlLimit);

            embed["author"] = authorObject;
        }

        // ISO 8601 UTC with Z suffix — invariant culture, no CurrentCulture anywhere (T-020 date rule).
        embed["timestamp"] = post.PublishedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // T-052/T-053: feed source — always present (label is the feed's display name when non-blank, else the required Post.SourceUrl) and
        // truncated to Discord's footer limit. Discord renders the footer on the same bottom line to the LEFT of the timestamp, i.e. exactly "before the date".
        embed["footer"] = new JsonObject { ["text"] = TruncateWithEllipsis("Feed: " + FeedLabel(post), FooterTextLimit) };

        var image = EffectiveImage(post, config);
        if (image is not null)                           // omitted when no effective image at all
            embed["image"] = new JsonObject { ["url"] = image };

        return embed;
    }

    /// <summary>Forum thread name: the post title, or — when the title is null/empty — the host of <c>post.SourceUrl</c> (the v1.0
    /// "feed name" fallback, e.g. "example.com"; documented in the T-020 report). A malformed SourceUrl must not crash delivery:
    /// the raw value is used as-is in that case (still truncated to the limit by the caller).</summary>
    private static string ThreadNameFor(Post post)
    {
        if (!string.IsNullOrWhiteSpace(post.Title))
            return post.Title;

        return Uri.TryCreate(post.SourceUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : post.SourceUrl;
    }

    /// <summary>T-053: feed attribution label — the feed's display name when non-blank, else the required SourceUrl (URL fallback);
    /// never null. Used by both the embed/forum footer and the markdown final section so they stay coherent.</summary>
    private static string FeedLabel(Post post) =>
        string.IsNullOrWhiteSpace(post.SourceName) ? post.SourceUrl : post.SourceName;

    /// <summary>The fallback image rule: the post's own image when non-empty, else the configured fallback (guarantees an image
    /// whenever resolution earlier in the pipeline left ImageUrl empty), else none.</summary>
    private static string? EffectiveImage(Post post, DiscordTargetConfig config)
        => !string.IsNullOrWhiteSpace(post.ImageUrl) ? post.ImageUrl : config.FallbackImageUrl;

    /// <summary>Shared HTML → plain-text helper used by every mode so stripping behavior is coherent across them: turns
    /// <c>&lt;br&gt;</c> variants into newlines, removes tags (replaced with a space so adjacent words do not merge), decodes common
    /// entities and numeric character references, then preserves line structure (per-line whitespace collapse + trim, 3+ consecutive
    /// newlines → one blank line). Returns null when the result is empty. T-013: the logic now lives in the shared
    /// <see cref="HtmlPlainText"/> helper used by every post builder — this public entry point delegates to it, keeping its signature
    /// and behavior unchanged (no double-decoding regression on already-decoded text).</summary>
    public static string? PlainText(string? html) => HtmlPlainText.Decode(html);

    /// <summary>Truncates to exactly <paramref name="limit"/> characters with a single trailing ellipsis when cutting; returns the
    /// value unchanged when it already fits. The result is never longer than the limit.</summary>
    public static string TruncateWithEllipsis(string value, int limit)
    {
        if (value.Length <= limit)
            return value;

        return value[..(limit - 1)] + "…";   // single trailing ellipsis, total length exactly `limit`
    }
}
