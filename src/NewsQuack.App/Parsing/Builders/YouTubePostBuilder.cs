using NewsQuack.App.Parsing;
using NewsQuack.Core;

namespace NewsQuack.App.Parsing.Builders;

/// <summary>
/// T-013: YouTube strategy — identical to the generic mapping except that <c>ImageUrl</c> prefers the item's
/// <c>&lt;media:group&gt;/&lt;media:thumbnail @url&gt;</c> value when present (resolved against the feed URL); items without a
/// thumbnail fall back to the generic first-&lt;img&gt;-in-description rule. Nothing else differs (T-013 decision 4).
/// </summary>
internal sealed class YouTubePostBuilder : IPostBuilder
{
    private readonly FeedItemMapper _generic;

    public YouTubePostBuilder(FeedDateParser dates, int? descriptionLimit = null) => _generic = new(dates, descriptionLimit);

    public Post? Build(RawFeedItem item, string sourceUrl)
    {
        var post = _generic.Build(item, sourceUrl);   // ItemDropException (missing title/date) propagates to the parser's per-item handler
        if (post is null) return null;

        // media:thumbnail wins over the generic first-<img> rule when present; blank/unresolvable ⇒ keep the generic result.
        var thumbnail = FeedItemMapper.ResolveLink(item.MediaThumbnailUrl, sourceUrl);   // null when absent or unresolvable
        return thumbnail is null ? post : post with { ImageUrl = thumbnail };
    }
}
