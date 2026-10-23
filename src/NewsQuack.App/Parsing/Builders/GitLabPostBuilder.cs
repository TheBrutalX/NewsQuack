using NewsQuack.App.Parsing;
using NewsQuack.Core;

namespace NewsQuack.App.Parsing.Builders;

/// <summary>
/// T-013: GitLab strategy (gitlab.com and self-hosted instances served under the <c>/-/feeds</c> convention). The mapping is
/// currently identical to the generic one — this type exists so platform selection is explicit and every platform has a single
/// home for future specialization (T-013 decision 4). Routing and output equivalence with the generic builder are pinned by
/// tests; no extra platform fields are invented beyond what the fixtures carry.
/// </summary>
internal sealed class GitLabPostBuilder : IPostBuilder
{
    private readonly FeedItemMapper _generic;

    public GitLabPostBuilder(FeedDateParser dates, int? descriptionLimit = null) => _generic = new(dates, descriptionLimit);

    public Post? Build(RawFeedItem item, string sourceUrl) => _generic.Build(item, sourceUrl);
}
