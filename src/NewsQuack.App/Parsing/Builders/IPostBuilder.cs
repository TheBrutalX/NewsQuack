using NewsQuack.App.Parsing;
using NewsQuack.Core;

namespace NewsQuack.App.Parsing.Builders;

/// <summary>
/// T-013: per-platform strategy that maps one raw feed item to a canonical <see cref="Post"/> (item data in ⇒ Post? out).
/// App-level internal contract — Core stays untouched. Selected explicitly from the feed source URL by the single
/// <see cref="PostBuilderSelector"/>; no platform checks exist anywhere else. Throws <see cref="ItemDropException"/> for
/// droppable cases (missing title/date), exactly like the T-012 mapper — the parser's per-item handler catches it.
/// Every builder produces <c>Post.Description</c> as decoded plain text through the shared <c>HtmlPlainText</c> helper.
/// </summary>
internal interface IPostBuilder
{
    Post? Build(RawFeedItem item, string sourceUrl);
}
