using NewsQuack.App.Parsing;

namespace NewsQuack.App.Parsing.Builders;

/// <summary>
/// T-013: the ONE place in the app where a platform strategy is chosen from a feed source URL (coordinator decision 2) — no
/// platform checks exist anywhere else. Documented rules, applied in order:
///   • YouTube when the host (case-insensitive) is <c>youtube.com</c> or <c>www.youtube.com</c>;
///   • Reddit when the host ends with <c>reddit.com</c> (covers the <c>old.</c>/<c>new.</c> subdomains);
///   • GitLab when the host is <c>gitlab.com</c> OR the path contains <c>/-/feeds</c> (GitLab's feed convention — covers
///     self-hosted instances such as gitlab.example.com);
///   • anything else, and any unparseable source URL ⇒ Generic. Selection never throws.
/// </summary>
internal sealed class PostBuilderSelector
{
    private readonly IPostBuilder _generic;   // fallback: everything not matched above (incl. unparseable URLs)
    private readonly IPostBuilder _youtube;
    private readonly IPostBuilder _reddit;
    private readonly IPostBuilder _gitlab;

    public PostBuilderSelector(IPostBuilder generic, IPostBuilder youtube, IPostBuilder reddit, IPostBuilder gitlab)
    {
        _generic = generic;
        _youtube = youtube;
        _reddit = reddit;
        _gitlab = gitlab;
    }

    /// <summary>Convenience overload: the default platform strategies around the given generic mapper. Used by the T-012
    /// test-facing <c>FeedParser</c> constructor, where no platform feed is expected to be parsed.</summary>
    public PostBuilderSelector(FeedItemMapper generic)
        : this(generic, new YouTubePostBuilder(new FeedDateParser(null)), new RedditPostBuilder(new FeedDateParser(null)), new GitLabPostBuilder(new FeedDateParser(null))) { }

    /// <summary>Selects the strategy for one feed source URL. Defensive parsing (Uri.TryCreate), case-insensitive host
    /// comparison, never throws — unparseable input simply yields the generic builder.</summary>
    public IPostBuilder Select(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return _generic;   // unparseable (incl. null/empty) ⇒ generic

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();   // trailing dots are not significant in hosts; compare case-insensitively

        if (host is "youtube.com" or "www.youtube.com")
            return _youtube;

        if (host.EndsWith("reddit.com", StringComparison.Ordinal))   // host already lowercased: reddit.com + old./new. subdomains
            return _reddit;

        if (host == "gitlab.com" || uri.AbsolutePath.Contains("/-/feeds", StringComparison.Ordinal))
            return _gitlab;   // the query string is deliberately NOT searched — only the path carries the convention

        return _generic;
    }
}
