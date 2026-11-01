using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NewsQuack.App.Parsing.Builders;
using NewsQuack.Core;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-012: the raw, per-item data a feed node yields before canonical mapping. Built identically by both
/// parsing passes (primary + tolerant) so the mapper below sees one shape regardless of which pass ran.
/// All fields are raw as found in the document — no resolution/normalization happens here.
/// </summary>
internal sealed record RawFeedItem(
    string? Title,                 // raw &lt;title&gt; text (may be null/empty for a broken item)
    string? Link,                  // raw link value (relative or absolute), null when absent
    string? GuidOrId,              // raw &lt;guid&gt;/&lt;id&gt; value, null when absent/empty
    string? RawDate,               // raw date string (published/pubDate, updated fallback), null when absent
    DateTimeOffset? LibraryDate,   // primary parser's already-UTC date (pass 1 only); null otherwise
    string? Description,           // raw HTML description/content text (one XML-decode level), null when absent
    string? RawImageSrc,           // src of the first real <img> element inside the description (unresolved), null when none
    string? MediaThumbnailUrl,     // T-013: @url of &lt;media:group&gt;/&lt;media:thumbnail&gt; (namespace-aware), null when absent
    PostAuthor? Author);           // resolved author (item-level with feed-level fallback), null when none

/// <summary>
/// T-012: raised by the mapper for an item that must be dropped for a *known* reason (missing title or
/// date). The pass catches it, logs a structured Warning with the reason, and counts it — without letting
/// one bad item take down the rest of the feed. Unexpected mapping faults surface as ordinary exceptions.
/// </summary>
internal sealed class ItemDropException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// T-012: pure mapper from a <see cref="RawFeedItem"/> to a canonical <see cref="Post"/> (item data in ⇒ Post? out). Shared by both passes so their field extraction is identical. Throws <see cref="ItemDropException"/> for the droppable cases; every other fault propagates as an ordinary exception.
/// T-013: this mapper IS the generic platform strategy — it implements <see cref="IPostBuilder"/>, and its mapping gained coherent HTML decoding of the description through the shared <see cref="HtmlPlainText"/> helper (with an optional plain-character limit, null in v1.0). Everything else is byte-identical to T-012: title required, date rules via FeedDateParser, link resolution, ItemId derivation guid→link→SHA-256 — and no NRE on any null optional field (a regression-pinned nullable-safety defect class).
/// </summary>
internal sealed class FeedItemMapper : IPostBuilder
{
    // First &lt;img src="..."/&gt; in the raw description HTML (double or single quoted). Presentation decoding is out of scope.
    private static readonly Regex ImgSrc = new("<img\\b[^>]*?\\bsrc\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly FeedDateParser _dates;
    private readonly int? _descriptionLimit;

    /// <param name="dates">Shared date parser (UTC-only rules).</param>
    /// <param name="descriptionLimit">T-013: optional plain-character cap applied AFTER decoding (no ellipsis at parse level).
    /// v1.0 registers null — no config key exists for it in the frozen T-002 schema.</param>
    public FeedItemMapper(FeedDateParser dates, int? descriptionLimit = null)
    {
        _dates = dates;
        _descriptionLimit = descriptionLimit;
    }

    /// <summary>Maps one raw item to a canonical post, or throws <see cref="ItemDropException"/> to drop it.</summary>
    public Post? Build(RawFeedItem item, string sourceUrl)
    {
        // Title is required: null/empty after trim ⇒ per-item error (never a silent empty title).
        var title = item.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            throw new ItemDropException("missing or empty title");

        // PublishedAt: own parser on the raw string first; pass-1 fallback to the library's already-UTC date.
        // Still null ⇒ drop — never a silent "now" (coordinator decision 3).
        var publishedAt = _dates.TryParse(item.RawDate) ?? item.LibraryDate;
        if (publishedAt is null)
            throw new ItemDropException("missing or unparseable date");

        var link = ResolveLink(item.Link, sourceUrl);   // nullable; relative resolved against the feed URL
        var itemId = DeriveItemId(item.GuidOrId, link, sourceUrl, title, item.RawDate);

        // Image extraction runs on the RAW description (before decoding) — byte-identical to T-012: prefer the real element's
        // src (attribute-based); fall back to a regex on the raw text for escaped &lt;img&gt;. Relative URLs resolve against
        // the feed URL (null on failure).
        var imageUrl = ResolveLink(item.RawImageSrc, sourceUrl) ?? ExtractImageUrl(item.Description, sourceUrl);

        return new Post
        {
            Id = Guid.NewGuid().ToString("D"),          // system-assigned unique id at construction time
            ItemId = itemId,                            // stable dedup key (v1.0 baseline derivation rule)
            Title = title,                              // non-null: threw above when null/empty after trim
            // T-013: decoded plain text through the one shared helper — null when absent/empty after decoding.
            Description = HtmlPlainText.Decode(item.Description, _descriptionLimit),
            Link = link,                                // nullable: items without a link build fine (no NRE)
            Author = item.Author,                       // nullable: no author ⇒ null
            PublishedAt = publishedAt.Value.ToUniversalTime(),   // always zero-offset UTC
            ImageUrl = imageUrl,                        // nullable: no image found ⇒ null
            SourceUrl = sourceUrl,
        };
    }

    /// <summary>Resolves a raw link to an absolute URL string; null when absent or unresolvable (never an error).</summary>
    internal static string? ResolveLink(string? rawLink, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(rawLink)) return null;

        var candidate = rawLink.Trim();

        // "Absolute" only when it carries an explicit non-file scheme (http/https/…). .NET otherwise misclassifies a
        // root-relative path like "/posts/42" as an absolute file:/// URI, which must instead resolve against the feed host.
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) && absolute.Scheme is not ("file" or ""))
            return absolute.ToString();   // already an absolute web URL — use as-is

        // Relative: resolve against the feed URL. Any failure ⇒ null (not an error), so callers never NRE.
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, candidate, out var resolved))
            return resolved.ToString();

        return null;
    }

    /// <summary>
    /// v1.0 baseline ItemId derivation (coordinator decision 4): guid/id element verbatim → absolute link →
    /// stable lowercase-hex SHA-256 of "sourceUrl|title|rawDateString". A later task may refine this only via a
    /// recorded commit decision.
    /// </summary>
    internal static string DeriveItemId(string? guidOrId, string? absoluteLink, string sourceUrl, string title, string? rawDate)
    {
        if (!string.IsNullOrWhiteSpace(guidOrId)) return guidOrId.Trim();       // (1) present & non-empty → verbatim (narrowed non-null)
        if (!string.IsNullOrWhiteSpace(absoluteLink)) return absoluteLink;      // (2) fall back to the absolute link (narrowed non-null)

        var input = $"{sourceUrl}|{title}|{rawDate ?? string.Empty}";           // (3) stable hash of the composite key
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();                    // lowercase hex
    }

    /// <summary>First &lt;img src="..."/&gt; in the raw description HTML, resolved against the feed URL; null when none.</summary>
    internal static string? ExtractImageUrl(string? description, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var match = ImgSrc.Match(description);
        if (!match.Success) return null;

        var src = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(src)) return null;

        return ResolveLink(src, sourceUrl);   // resolve relative image URLs against the feed URL; null on failure
    }
}
