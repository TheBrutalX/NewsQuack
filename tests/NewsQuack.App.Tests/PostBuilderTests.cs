using System.Text.RegularExpressions;
using NewsQuack.App.Parsing;
using NewsQuack.App.Parsing.Builders;
using NewsQuack.Core;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-013: tests of the per-platform IPostBuilder strategies — explicit selection by PostBuilderSelector (the only place in
/// the app where a platform is chosen), platform behaviors pinned against the shared fixtures, coherent HTML decoding in all
/// branches and nullable-safe mapping (a nullable-safety regression class). T-012's FeedParserTests remain the unchanged
/// behavior contract for the generic path.
/// </summary>
public class PostBuilderTests
{
    private const string Source = "https://example.com/feed.xml";

    // ── Selector (unit) ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Select_YouTubeHosts_ReturnYouTubeBuilder()
    {
        var selectors = CreateSelectors();

        Assert.IsType<YouTubePostBuilder>(selectors.Selector.Select("https://youtube.com/feeds/videos.xml"));
        Assert.IsType<YouTubePostBuilder>(selectors.Selector.Select("https://www.youtube.com/feeds/videos.xml?channel_id=UCd9fKqLmNpRsTuVwXyZaBcD"));   // query string does not affect host matching
        Assert.IsType<YouTubePostBuilder>(selectors.Selector.Select("https://WWW.YouTube.Com/feeds/videos.xml"));   // case-insensitive
    }

    [Fact]
    public void Select_RedditSubdomains_ReturnRedditBuilder()
    {
        var selectors = CreateSelectors();

        Assert.IsType<RedditPostBuilder>(selectors.Selector.Select("https://old.reddit.com/r/rust/feed"));
        Assert.IsType<RedditPostBuilder>(selectors.Selector.Select("https://new.reddit.com/r/rust/.rss"));
        Assert.IsType<RedditPostBuilder>(selectors.Selector.Select("https://reddit.com/r/rust/feed"));   // bare host ends with reddit.com too
    }

    [Fact]
    public void Select_GitLabComAndSelfHostedFeedsPath_ReturnGitLabBuilder()
    {
        var selectors = CreateSelectors();

        Assert.IsType<GitLabPostBuilder>(selectors.Selector.Select("https://gitlab.com/acme/payments-service/-/feeds"));   // host rule
        Assert.IsType<GitLabPostBuilder>(selectors.Selector.Select("https://gitlab.example.com/acme/payments-service/-/feeds"));   // self-hosted: /-/feeds path convention

        // Self-hosted WITHOUT the /-/feeds path is not GitLab by the documented rules ⇒ generic.
        Assert.IsType<FeedItemMapper>(selectors.Selector.Select("https://gitlab.example.com/acme/payments-service"));
    }

    [Fact]
    public void Select_UnknownHostOrUnparseableUrl_ReturnGenericBuilderNeverThrows()
    {
        var selectors = CreateSelectors();

        Assert.IsType<FeedItemMapper>(selectors.Selector.Select("https://example.com/feed.xml"));
        Assert.IsType<FeedItemMapper>(selectors.Selector.Select("ftp://example.com/feed"));   // parseable, but no platform rule matches
        Assert.IsType<FeedItemMapper>(selectors.Selector.Select("not a url at all"));         // unparseable ⇒ generic, never throws
        Assert.IsType<FeedItemMapper>(selectors.Selector.Select(string.Empty));               // empty ⇒ generic, never throws
    }

    // ── Platform builders (full parser with platform source URLs) ────────────────────────────────────────────

    [Fact]
    public async Task Parse_YouTubeFixture_MediaThumbnailUsedAsImageUrl()
    {
        var (parser, _) = CreatePlatformParser();

        const string source = "https://www.youtube.com/feeds/videos.xml?channel_id=UCd9fKqLmNpRsTuVwXyZaBcD";
        var result = await parser.ParseAsync(FixtureLoader.Load("youtube.xml"), source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        Assert.Equal(3, result.Posts.Count);

        // The item's media:thumbnail wins as ImageUrl — the fixture's exact i.ytimg.com values.
        Assert.Equal(
            ["https://i.ytimg.com/vi/aB3dE5fGh7i/hqdefault.jpg", "https://i.ytimg.com/vi/jK9mL2nOp4q/hqdefault.jpg", "https://i.ytimg.com/vi/rSt6uVwXyZ8/hqdefault.jpg"],
            result.Posts.Select(p => p.ImageUrl).ToList());

        // ItemIds remain the tag:youtube.com guid values verbatim.
        Assert.Equal(
            ["tag:youtube.com,2016:video:aB3dE5fGh7i", "tag:youtube.com,2016:video:jK9mL2nOp4q", "tag:youtube.com,2016:video:rSt6uVwXyZ8"],
            result.Posts.Select(p => p.ItemId).ToList());
    }

    [Fact]
    public async Task Parse_YouTubeItemWithoutMediaGroup_FallsBackToImgInDescription()
    {
        var (parser, _) = CreatePlatformParser();

        const string feed = """
            <rss version="2.0"><channel><title>YT fallback</title>
              <item><title>Fallback video</title>
                <link>https://www.youtube.com/watch?v=abc123</link>
                <guid isPermaLink="false">tag:youtube.com,2016:video:abc123</guid>
                <pubDate>Tue, 21 Oct 2025 16:42:10 +0000</pubDate>
                <description>Watch the full build &amp; grab the files.
                  <img src="https://i.ytimg.com/vi/abc123/hqdefault.jpg"/></description>
              </item></channel></rss>
            """;

        var result = await parser.ParseAsync(feed, "https://www.youtube.com/feeds/videos.xml?channel_id=UCd9fKqLmNpRsTuVwXyZaBcD");

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        var post = Assert.Single(result.Posts);

        // No media:group ⇒ MediaThumbnailUrl is null (no NRE) ⇒ the generic first-<img>-in-description rule applies.
        Assert.Equal("https://i.ytimg.com/vi/abc123/hqdefault.jpg", post.ImageUrl);
    }

    [Fact]
    public async Task Parse_RedditFixture_PlatformSourceUrl_OutputEquivalentToGenericBuilder()
    {
        var (parser, _) = CreatePlatformParser();

        var xml = FixtureLoader.Load("reddit.xml");

        // Same XML parsed with a platform source URL (⇒ RedditPostBuilder) vs. a neutral one (⇒ generic): field-for-field equal.
        var viaPlatform = await parser.ParseAsync(xml, "https://old.reddit.com/r/rust/feed");
        var viaGeneric = await parser.ParseAsync(xml, "https://example.com/reddit.xml");

        Assert.Null(viaPlatform.FeedError);
        Assert.Equal(0, viaPlatform.ItemErrorCount);
        Assert.Equal(FullKey(viaGeneric.Posts), FullKey(viaPlatform.Posts));   // SourceUrl excluded — it legitimately differs between the two runs
    }

    [Fact]
    public async Task Parse_GitLabFixture_PlatformSourceUrl_OutputEquivalentToGenericBuilder()
    {
        var (parser, _) = CreatePlatformParser();

        var xml = FixtureLoader.Load("gitlab.xml");

        // Same XML parsed with a self-hosted /-/feeds URL (⇒ GitLabPostBuilder) vs. a neutral one (⇒ generic): field-for-field equal.
        var viaPlatform = await parser.ParseAsync(xml, "https://gitlab.example.com/acme/payments-service/-/feeds");
        var viaGeneric = await parser.ParseAsync(xml, "https://example.com/gitlab.xml");

        Assert.Null(viaPlatform.FeedError);
        Assert.Equal(0, viaPlatform.ItemErrorCount);
        Assert.Equal(FullKey(viaGeneric.Posts), FullKey(viaPlatform.Posts));   // SourceUrl excluded — it legitimately differs between the two runs
    }

    // ── Decoding: fixture descriptions come out tag-free through every builder ───────────────────────────────

    [Fact]
    public async Task Build_GenericFixtureDescriptions_DecodedTagFree()
    {
        var (parser, _) = CreatePlatformParser();

        var rss2 = await parser.ParseAsync(FixtureLoader.Load("rss2.xml"), Source);
        Assert.Null(rss2.FeedError);

        // Exact decoded plain text — no residual tags or entities. Item 2's <guid …> is literal feed content (double-escaped in
        // the source), not residual markup; item 4's image stays on ImageUrl, out of the description. T-055: item 1's literal
        // newline survives as a real line break (the img-only final line trims away).
        Assert.Equal(
            [
                "The new release cuts build times by 30% & fixes a regression in the optimizer.\nSee the full changelog for details.",
                "What <guid isPermaLink=\"true\"> actually means and how to validate feeds before parsing them.",
                "We deployed at local midnight to avoid the traffic peak. Numbers & lessons inside.",
                "Join us & ask anything about item IDs, GUIDs & at-least-once delivery.",
            ],
            rss2.Posts.Select(p => p.Description).ToList());

        var atom = await parser.ParseAsync(FixtureLoader.Load("atom.xml"), Source);
        Assert.Null(atom.FeedError);

        // Entry 3's <item> is likewise literal feed content (double-escaped in the source).
        Assert.Equal(
            [
                "CI pipelines that ship and test in parallel cut lead time. Here is how we did it & what broke.",
                "A dual-read strategy: run old & new parsers side by side, diff outputs, then cut over.",
                "One <item> with an unclosed tag took down the whole feed. Per-item error isolation is the fix.",
                "A crash between send & confirm may produce one duplicate post — never a lost one.",
            ],
            atom.Posts.Select(p => p.Description).ToList());

        Assert.All(rss2.Posts.Concat(atom.Posts), p => Assert.DoesNotMatch(ResidualEntity, p.Description ?? string.Empty));
    }

    [Fact]
    public async Task Build_MultiLineDescFixture_CvefeedSampleDecodedEndToEnd()
    {
        // T-055 (issue #71): the real parse path (FeedItemMapper → HtmlPlainText.Decode) preserves the canonical cvefeed
        // sample's line structure byte-for-byte — labelled sections on separate lines, one blank line between them.
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("rss2-multiline-desc.xml"), Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);

        var post = Assert.Single(result.Posts);
        Assert.Equal(
            "CVE ID : CVE-2026-78674\n\nPublished : Mercoledì 28 Ottobre 2026 19:21 | 28 minuti ago\n\nDescription : The BPMN System REST APIs allow an attacker to bypass access controls.\n\nAn authenticated user can leverage this flaw to read arbitrary data.\n--\nVisit the link for more details about this vulnerability.",
            post.Description);
    }

    [Fact]
    public async Task Build_YouTubeFixtureDescriptions_DecodedTagFree()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("youtube.xml"), "https://www.youtube.com/feeds/videos.xml?channel_id=UCd9fKqLmNpRsTuVwXyZaBcD");
        Assert.Null(result.FeedError);

        Assert.Equal(
            [
                "Gantry squareness checks, rail mounting & the first axis move. Full parts list in the description.",
                "A $30 fume extractor that actually works — build files & airflow measurements inside.",
                "Rigid does not mean stiff. Modal analysis of the old frame & the redesigned X carriage.",
            ],
            result.Posts.Select(p => p.Description).ToList());

        Assert.All(result.Posts, p => Assert.DoesNotMatch(ResidualEntity, p.Description!));
    }

    [Fact]
    public async Task Build_RedditFixtureDescriptions_DecodedTagFree()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("reddit.xml"), "https://old.reddit.com/r/rust/feed");
        Assert.Null(result.FeedError);

        // The escaped <p>/<code> wrappers are stripped; the &amp;amp; double-escape decodes to a single &.
        Assert.Equal(
            [
                "Zero dependencies, one binary, & a test suite that runs in 200ms. Feedback welcome.",
                "When does a thiserror enum beat a trait object? Benchmarks inside.",
                "The borrow checker is not your enemy — it is a very strict code reviewer.",
            ],
            result.Posts.Select(p => p.Description).ToList());

        Assert.All(result.Posts, p => Assert.DoesNotMatch(ResidualEntity, p.Description!));
    }

    [Fact]
    public async Task Build_GitLabFixtureDescriptions_DecodedTagFree()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("gitlab.xml"), "https://gitlab.example.com/acme/payments-service/-/feeds");
        Assert.Null(result.FeedError);

        // The fixture carries no content/summary elements ⇒ every description is null (vacuously tag-free).
        Assert.All(result.Posts, p => Assert.Null(p.Description));
    }

    // ── Nullable-safety (regression-pinned defect class) ──────────────────────────────────────────────────────

    [Fact]
    public void Build_ItemWithoutLink_BuildsPostWithNullLinkNoNre()
    {
        var builder = new FeedItemMapper(new FeedDateParser());

        // Every optional field null/absent — must build successfully, never NRE.
        var raw = new RawFeedItem("Title only", null, null, "2025-10-21T14:30:00Z", null, null, null, null, null);

        var post = builder.Build(raw, Source);

        Assert.NotNull(post);
        Assert.Equal("Title only", post!.Title);
        Assert.Null(post.Link);            // no link ⇒ null (not an error)
        Assert.Null(post.Description);     // no description ⇒ null after decoding
        Assert.Null(post.Author);          // no author ⇒ null
        Assert.Null(post.ImageUrl);        // no image anywhere ⇒ null
    }

    [Fact]
    public void Build_ItemWithoutGuidOrId_UsesStableHashItemId()
    {
        var builder = new FeedItemMapper(new FeedDateParser());
        var raw = new RawFeedItem("Some Title", null, null, "2025-10-21T14:30:00Z", null, null, null, null, null);

        var first = builder.Build(raw, Source)!;
        var second = builder.Build(raw, Source)!;   // same raw item built twice ⇒ same stable ItemId (system Id differs)

        Assert.Matches("^[0-9a-f]{64}$", first.ItemId);                        // falls through to the SHA-256 rule: 64 lowercase-hex chars
        Assert.Equal(first.ItemId, second.ItemId);                             // deterministic
        Assert.NotEqual(first.Id, second.Id);                                  // system-assigned id is per-construction
        Assert.Equal(FeedItemMapper.DeriveItemId(null, null, Source, "Some Title", "2025-10-21T14:30:00Z"), first.ItemId);   // the documented rule
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Residual HTML entity in an already-decoded description (named or numeric) — none may survive decoding.</summary>
    private static readonly Regex ResidualEntity = new("&[a-zA-Z][a-zA-Z0-9]*;|&#x?[0-9a-fA-F]+;", RegexOptions.Compiled);

    private static (PostBuilderSelector Selector, FeedItemMapper Generic, YouTubePostBuilder YouTube, RedditPostBuilder Reddit, GitLabPostBuilder GitLab) CreateSelectors()
    {
        var dates = new FeedDateParser();
        var generic = new FeedItemMapper(dates);
        var youtube = new YouTubePostBuilder(dates);
        var reddit = new RedditPostBuilder(dates);
        var gitlab = new GitLabPostBuilder(dates);

        return (new PostBuilderSelector(generic, youtube, reddit, gitlab), generic, youtube, reddit, gitlab);
    }

    private static (FeedParser Parser, PostBuilderSelector Selector) CreatePlatformParser()
    {
        var logs = new LogCapture();
        var dates = new FeedDateParser(logs.For<FeedDateParser>());

        var selector = new PostBuilderSelector(
            new FeedItemMapper(dates),                       // generic — v1.0: no description limit (null)
            new YouTubePostBuilder(dates),
            new RedditPostBuilder(dates),
            new GitLabPostBuilder(dates));

        return (new FeedParser(logs.For<FeedParser>(), selector), selector);
    }

    /// <summary>Full comparable projection of a post — every field except the random per-construction Id and the run-specific SourceUrl.</summary>
    private static List<(string ItemId, string Title, string? Description, string? Link, string? AuthorName, string? AuthorUrl, DateTimeOffset At, string? ImageUrl)> FullKey(IReadOnlyList<Post> posts) =>
        posts.Select(p => (p.ItemId, p.Title, p.Description, p.Link, p.Author?.Name, p.Author?.Url, p.PublishedAt, p.ImageUrl)).ToList();
}
