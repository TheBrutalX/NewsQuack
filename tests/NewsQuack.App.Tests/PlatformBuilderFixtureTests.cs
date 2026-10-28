using NewsQuack.App.Parsing;
using NewsQuack.App.Parsing.Builders;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // LogLevel for the drop-observability assertion
using Xunit;
using Xunit.Sdk;   // XunitException for the per-field diff message

namespace NewsQuack.App.Tests;

/// <summary>
/// T-043: field-by-field pinning of the exact <see cref="Post"/> output of the per-platform builders — every post of all
/// five shared fixtures (each parsed through a platform-appropriate source URL so the real strategy executes) and broken-item
/// survival at full granularity. Every asserted value is an exact pinned constant: computed once by running the parser against
/// the fixture, then reviewed for sanity (no residual markup in descriptions, authors match the fixture XML, dates match
/// <see cref="TzCellReferenceTable"/>, ItemIds are the guid/id values verbatim) and frozen here. The two SHA-256 ItemId pins were
/// additionally verified independently (sha256sum of the documented "sourceUrl|title|rawDate" derivation input). T-012's
/// FeedParserTests and T-013's PostBuilderTests keep their narrower contracts; this file goes deeper (every field, exact values)
/// and wider (broken items next to healthy ones, asserted field-by-field).
/// </summary>
public class PlatformBuilderFixtureTests
{
    // ── Platform-appropriate source URLs (T-043 decision 1): the real strategy must execute for every fixture run ──

    private const string Rss2Source = "https://example.com/rss2.xml";                                   // generic host ⇒ FeedItemMapper
    private const string AtomSource = "https://example.com/atom.xml";                                   // generic host ⇒ FeedItemMapper
    private const string YouTubeSource = "https://www.youtube.com/feeds/videos.xml?channel_id=UCd9fKqLmNpRsTuVwXyZaBcD";   // ⇒ YouTubePostBuilder
    private const string RedditSource = "https://www.reddit.com/r/rust/.rss";                           // host ends with reddit.com ⇒ RedditPostBuilder
    private const string GitLabSource = "https://gitlab.example.com/acme/payments-service/-/feeds/all.atom.xml";   // /-/feeds path ⇒ GitLabPostBuilder
    private const string GenericSource = "https://example.com/feed.xml";                                // inline broken-item scenarios (generic)
    private const string Rss2BrokenItemSource = "https://example.com/rss2-broken-item.xml";

    // Pinned SHA-256 ItemIds — deterministic hashes of the documented derivation input "sourceUrl|title|rawDate"
    // (FeedItemMapper.DeriveItemId rule 3), independently verified with sha256sum over UTF-8 bytes:
    private const string PinnedHashGeneric = "3047f4c67cfb90df936e28c494d5e0fb4f36f8a905c50a09f0c7f5765bc2ac8e";   // GenericSource|Hashed item|Tue, 21 Oct 2025 16:44:00 +0000
    private const string PinnedHashYouTube = "7bcc3b05a6b518deb9a6472c4945c3790a6bf6982885dd22b070382d1dc33f01";   // YouTubeSource|No guid video|Tue, 21 Oct 2025 16:49:00 +0000

    private const string IdPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";   // system-assigned GUID string (decision 1)

    // ── Field-by-field fixture tests (decision 1) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_Rss2_EveryFieldOfEveryPostExact()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("rss2.xml"), Rss2Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        AssertEveryFieldExact(result.Posts, ExpectedRss2, Rss2Source);
    }

    [Fact]
    public async Task Parse_Atom_EveryFieldOfEveryPostExact()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("atom.xml"), AtomSource);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        AssertEveryFieldExact(result.Posts, ExpectedAtom, AtomSource);
    }

    [Fact]
    public async Task Parse_YouTube_EveryFieldOfEveryPostExact()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("youtube.xml"), YouTubeSource);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        AssertEveryFieldExact(result.Posts, ExpectedYouTube, YouTubeSource);
    }

    [Fact]
    public async Task Parse_Reddit_EveryFieldOfEveryPostExact()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("reddit.xml"), RedditSource);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        AssertEveryFieldExact(result.Posts, ExpectedReddit, RedditSource);
    }

    [Fact]
    public async Task Parse_GitLab_EveryFieldOfEveryPostExact()
    {
        var (parser, _) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("gitlab.xml"), GitLabSource);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        AssertEveryFieldExact(result.Posts, ExpectedGitLab, GitLabSource);
    }

    // ── Broken-item survival tests (decision 2) — inline XML; the shared fixtures stay untouched ───────────

    [Fact]
    public async Task Parse_ItemWithoutLink_BuildsWithNullLinkOthersSurviveExactly()
    {
        var (parser, _) = CreatePlatformParser();

        const string feed = """
            <rss version="2.0"><channel><title>NoLink</title>
              <item><title>Healthy sibling</title>
                <link>https://example.com/sibling</link>
                <guid isPermaLink="true">https://example.com/sibling</guid>
                <pubDate>Tue, 21 Oct 2025 16:42:10 +0000</pubDate>
                <description>Sibling description &amp; notes.</description>
              </item>
              <item><title>No link item</title>
                <guid isPermaLink="false">no-link-item-guid</guid>
                <pubDate>Tue, 21 Oct 2025 16:43:00 +0000</pubDate>
                <description>An item without any link element.</description>
              </item></channel></rss>
            """;

        var result = await parser.ParseAsync(feed, GenericSource);

        Assert.Null(result.FeedError);             // a missing link is not an error — the item builds with Link == null
        Assert.Equal(0, result.ItemErrorCount);    // nothing dropped: both items are valid
        AssertEveryFieldExact(result.Posts,
        [
            new("https://example.com/sibling", "Healthy sibling", "Sibling description & notes.", "https://example.com/sibling", null, Utc(2025, 10, 21, 16, 42, 10), null),
            new("no-link-item-guid", "No link item", "An item without any link element.", null, null, Utc(2025, 10, 21, 16, 43), null),   // Link == null; ItemId is the guid verbatim
        ], GenericSource);
    }

    [Fact]
    public async Task Parse_ItemWithoutGuidOrId_UsesPinnedHashItemIdOthersSurviveExactly()
    {
        var (parser, _) = CreatePlatformParser();

        const string feed = """
            <rss version="2.0"><channel><title>NoGuid</title>
              <item><title>Hashed item</title>
                <pubDate>Tue, 21 Oct 2025 16:44:00 +0000</pubDate>
                <description>An item without guid, id or link.</description>
              </item>
              <item><title>Healthy sibling two</title>
                <link>https://example.com/sibling-two</link>
                <guid isPermaLink="true">https://example.com/sibling-two</guid>
                <pubDate>Tue, 21 Oct 2025 16:45:00 +0000</pubDate></item></channel></rss>
            """;

        var first = await parser.ParseAsync(feed, GenericSource);
        var second = await parser.ParseAsync(feed, GenericSource);   // same feed parsed again ⇒ identical ItemId (determinism)

        Assert.Null(first.FeedError);
        Assert.Equal(0, first.ItemErrorCount);
        AssertEveryFieldExact(first.Posts, ExpectedNoGuidSurvivors(PinnedHashGeneric), GenericSource);
        AssertEveryFieldExact(second.Posts, ExpectedNoGuidSurvivors(PinnedHashGeneric), GenericSource);   // second parse: byte-identical survivors

        // The pinned constant is the exact SHA-256 of the documented derivation input — cross-checked against the rule itself.
        Assert.Equal(PinnedHashGeneric, FeedItemMapper.DeriveItemId(null, null, GenericSource, "Hashed item", "Tue, 21 Oct 2025 16:44:00 +0000"));
    }

    [Fact]
    public async Task Parse_ItemWithMissingDate_DroppedOthersSurviveExactly()
    {
        var (parser, logs) = CreatePlatformParser();

        const string feed = """
            <rss version="2.0"><channel><title>NoDate</title>
              <item><title>Dated sibling</title>
                <link>https://example.com/dated-sibling</link>
                <guid isPermaLink="true">https://example.com/dated-sibling</guid>
                <pubDate>Tue, 21 Oct 2025 16:46:00 +0000</pubDate></item>
              <item><title>No date item</title>
                <link>https://example.com/no-date</link>
                <guid isPermaLink="false">no-date-item-guid</guid></item></channel></rss>
            """;

        var result = await parser.ParseAsync(feed, GenericSource);

        Assert.Null(result.FeedError);                 // per-item defect, not whole-feed
        Assert.Equal(1, result.ItemErrorCount);        // exactly the date-less item — dropped, never stamped "now"
        Assert.NotNull(logs.Find(LogLevel.Warning, "Dropping feed item"));   // the drop is observable structured state

        AssertEveryFieldExact(result.Posts,
        [
            new("https://example.com/dated-sibling", "Dated sibling", null, "https://example.com/dated-sibling", null, Utc(2025, 10, 21, 16, 46), null),
        ], GenericSource);
    }

    [Fact]
    public async Task Parse_YouTubeSourceUrl_BrokenItemVariants_NullSafeLikeGeneric()
    {
        var (parser, logs) = CreatePlatformParser();

        // The same three broken shapes as the generic scenarios, parsed under a YouTube source URL (⇒ YouTubePostBuilder):
        // no link; no guid/id/link; missing date. Identical nullable-safe behavior — and where an item has no media:group,
        // the thumbnail still falls back to the first <img> in the description.
        const string feed = """
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/"><channel><title>YT broken</title>
              <item><title>Healthy video</title>
                <link>https://www.youtube.com/watch?v=ok1</link>
                <guid isPermaLink="false">tag:youtube.com,2016:video:ok1</guid>
                <pubDate>Tue, 21 Oct 2025 16:47:00 +0000</pubDate>
                <description>Healthy video description.</description>
                <media:group><media:thumbnail url="https://i.ytimg.com/vi/ok1/hqdefault.jpg"/></media:group>
              </item>
              <item><title>No link video</title>
                <guid isPermaLink="false">tag:youtube.com,2016:video:nolink</guid>
                <pubDate>Tue, 21 Oct 2025 16:48:00 +0000</pubDate>
                <description>No link video description.
                  <img src="https://i.ytimg.com/vi/nolink/hqdefault.jpg"/></description>
              </item>
              <item><title>No guid video</title>
                <pubDate>Tue, 21 Oct 2025 16:49:00 +0000</pubDate>
                <description>No guid video description.</description>
                <media:group><media:thumbnail url="https://i.ytimg.com/vi/noguid/hqdefault.jpg"/></media:group>
              </item>
              <item><title>No date video</title>
                <link>https://www.youtube.com/watch?v=nodate</link>
                <guid isPermaLink="false">tag:youtube.com,2016:video:nodate</guid></item></channel></rss>
            """;

        var result = await parser.ParseAsync(feed, YouTubeSource);

        Assert.Null(result.FeedError);
        Assert.Equal(1, result.ItemErrorCount);        // exactly the date-less item dropped
        Assert.NotNull(logs.Find(LogLevel.Warning, "Dropping feed item"));

        AssertEveryFieldExact(result.Posts,
        [
            new("tag:youtube.com,2016:video:ok1", "Healthy video", "Healthy video description.", "https://www.youtube.com/watch?v=ok1", null, Utc(2025, 10, 21, 16, 47),
                "https://i.ytimg.com/vi/ok1/hqdefault.jpg"),                                                              // media:thumbnail wins
            new("tag:youtube.com,2016:video:nolink", "No link video", "No link video description.", null, null, Utc(2025, 10, 21, 16, 48),
                "https://i.ytimg.com/vi/nolink/hqdefault.jpg"),                                                           // Link == null; no media:group ⇒ <img> fallback
            new(PinnedHashYouTube, "No guid video", "No guid video description.", null, null, Utc(2025, 10, 21, 16, 49),
                "https://i.ytimg.com/vi/noguid/hqdefault.jpg"),                                                           // pinned hash ItemId; media:thumbnail wins
        ], YouTubeSource);

        Assert.Equal(PinnedHashYouTube, FeedItemMapper.DeriveItemId(null, null, YouTubeSource, "No guid video", "Tue, 21 Oct 2025 16:49:00 +0000"));
    }

    [Fact]
    public async Task Parse_Rss2BrokenItemFixture_SurvivorsFieldByFieldExact()
    {
        var (parser, logs) = CreatePlatformParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("rss2-broken-item.xml"), Rss2BrokenItemSource);

        Assert.Null(result.FeedError);                 // the defect is per-item, not whole-feed
        Assert.Equal(1, result.ItemErrorCount);        // exactly the title-less item (T-012 pins count/links; this closes field granularity)
        Assert.NotNull(logs.Find(LogLevel.Warning, "Dropping feed item"));

        // The three surviving posts — note their descriptions differ from rss2.xml's (no <img>, shorter text), so every value is pinned here.
        AssertEveryFieldExact(result.Posts, ExpectedRss2BrokenItemSurvivors, Rss2BrokenItemSource);
    }

    // ── Pinned expected projections (document order) ────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<ExpectedPost> ExpectedRss2 =
    [
        new("https://example.com/blog/compiler-release", "New compiler release & performance notes",
            "The new release cuts build times by 30% & fixes a regression in the optimizer.\nSee the full changelog for details.",   // T-055: the fixture's literal newline survives as a line break (img-only final line trims away)
            "https://example.com/blog/compiler-release", new PostAuthor("Alice Nguyen", null), Utc(2025, 10, 21, 23, 42, 10),
            "https://example.com/img/compiler-chart.png"),
        new("https://example.com/blog/rss-validation", "A practical guide to RSS validation",
            @"What <guid isPermaLink=""true""> actually means and how to validate feeds before parsing them.",   // the <guid …> is literal feed content (double-escaped in the source), not residual markup
            "https://example.com/blog/rss-validation", new PostAuthor("Alice Nguyen", null), Utc(2025, 10, 21, 16, 15, 44), null),
        new("https://example.com/blog/midnight-deploy", "Shipping at 00:05 <why not?>",
            "We deployed at local midnight to avoid the traffic peak. Numbers & lessons inside.",
            "https://example.com/blog/midnight-deploy", new PostAuthor("Bob Fischer", null), Utc(2025, 10, 21, 7, 5, 12), null),
        new("https://example.com/blog/community-call-dedup", "Community call: Q&A on feed deduplication",
            "Join us & ask anything about item IDs, GUIDs & at-least-once delivery.",
            "https://example.com/blog/community-call-dedup", new PostAuthor("Carol Diaz", null), Utc(2025, 10, 21, 1, 30),
            "https://example.com/img/community-call.png"),
    ];

    private static readonly IReadOnlyList<ExpectedPost> ExpectedAtom =
    [
        new("urn:uuid:1a2b3c4d-0001-4f5e-9a8b-7c6d5e4f3a2b", "Shipping & testing in parallel",
            "CI pipelines that ship and test in parallel cut lead time. Here is how we did it & what broke.",
            "https://example.org/blog/shipping-testing", new PostAuthor("Maria Weber", "https://example.org/maria"), Utc(2025, 10, 21, 14, 30), null),   // entry has no <author> ⇒ feed-level fallback
        new("urn:uuid:1a2b3c4d-0002-4f5e-9a8b-7c6d5e4f3a2b", "Migrating a legacy parser without downtime",
            "A dual-read strategy: run old & new parsers side by side, diff outputs, then cut over.",
            "https://example.org/blog/parser-migration", new PostAuthor("Peter Novak", "https://example.org/peter"), Utc(2025, 10, 21, 11, 58, 12), null),
        new("urn:uuid:1a2b3c4d-0003-4f5e-9a8b-7c6d5e4f3a2b", "Post-mortem: the feed that ate a weekend",
            "One <item> with an unclosed tag took down the whole feed. Per-item error isolation is the fix.",   // the <item> is literal feed content (double-escaped), not residual markup
            "https://example.org/blog/feed-postmortem", new PostAuthor("Maria Weber", "https://example.org/maria"), Utc(2025, 10, 20, 9, 15, 44), null),   // feed-level fallback again
        new("urn:uuid:1a2b3c4d-0004-4f5e-9a8b-7c6d5e4f3a2b", "Notes on at-least-once delivery",
            "A crash between send & confirm may produce one duplicate post — never a lost one.",
            "https://example.org/blog/at-least-once", new PostAuthor("Maria Weber", "https://example.org/maria"), Utc(2025, 10, 19, 21, 58, 7), null),   // entry-level author (same person as the feed level)
    ];

    private static readonly IReadOnlyList<ExpectedPost> ExpectedYouTube =
    [
        new("tag:youtube.com,2016:video:aB3dE5fGh7i", "CNC router build log #12 — gantry & rails",
            "Gantry squareness checks, rail mounting & the first axis move. Full parts list in the description.",
            "https://www.youtube.com/watch?v=aB3dE5fGh7i", null, Utc(2025, 10, 21, 16, 42, 10),
            "https://i.ytimg.com/vi/aB3dE5fGh7i/hqdefault.jpg"),   // media:thumbnail wins as ImageUrl
        new("tag:youtube.com,2016:video:jK9mL2nOp4q", "Soldering fume extraction on a budget",
            "A $30 fume extractor that actually works — build files & airflow measurements inside.",
            "https://www.youtube.com/watch?v=jK9mL2nOp4q", null, Utc(2025, 10, 19, 18, 5, 33),
            "https://i.ytimg.com/vi/jK9mL2nOp4q/hqdefault.jpg"),
        new("tag:youtube.com,2016:video:rSt6uVwXyZ8", "Why my first 3D printer frame failed (and the fix)",
            "Rigid does not mean stiff. Modal analysis of the old frame & the redesigned X carriage.",
            "https://www.youtube.com/watch?v=rSt6uVwXyZ8", null, Utc(2025, 10, 17, 12, 30),
            "https://i.ytimg.com/vi/rSt6uVwXyZ8/hqdefault.jpg"),
    ];

    private static readonly IReadOnlyList<ExpectedPost> ExpectedReddit =
    [
        new("t3_1mxyz9a", "Show /r/rust: I built a CLI in 500 lines of Rust in /r/rust",
            "Zero dependencies, one binary, & a test suite that runs in 200ms. Feedback welcome.",   // the &amp;amp; double-escape decodes to a single &
            "https://www.reddit.com/r/rust/comments/1mxyz9a/show_r_rust_i_built_a_cli_in_500_lines_of_rust/", new PostAuthor("/u/crate_roller", null), Utc(2025, 10, 21, 16, 39, 58, ms: 456), null),
        new("t3_1mwq8zb", "Traits vs. enums for error handling in /r/rust",
            "When does a thiserror enum beat a trait object? Benchmarks inside.",
            "https://www.reddit.com/r/rust/comments/1mwq8zb/traits_vs_enums_for_error_handling/", new PostAuthor("/u/borrow_checker_fan", null), Utc(2025, 10, 21, 14, 12, 47, ms: 901), null),
        new("t3_1mvn7ya", "Rust for C++ developers: the first week in /r/rust",
            "The borrow checker is not your enemy — it is a very strict code reviewer.",
            "https://www.reddit.com/r/rust/comments/1mvn7ya/rust_for_c_developers_the_first_week/", new PostAuthor("/u/migrating_from_cpp", null), Utc(2025, 10, 20, 21, 45, 19, ms: 664), null),
    ];

    private static readonly IReadOnlyList<ExpectedPost> ExpectedGitLab =
    [
        new("tag:gitlab.example.com,2025:acme/payments-service/commit/a3f8c9d2e4b1", "Commit a3f8c9d2e4b1 — Fix idempotency key collision on retries",
            null,   // the fixture carries no content/summary elements ⇒ every description is null
            "https://gitlab.example.com/acme/payments-service/-/commit/a3f8c9d2e4b1", new PostAuthor("Dana Whitfield", "https://gitlab.example.com/dwhitfield"), Utc(2025, 10, 21, 16, 40, 7), null),
        new("tag:gitlab.example.com,2025:acme/payments-service/release/v2025.10.1", "Release v2025.10.1",
            null,
            "https://gitlab.example.com/acme/payments-service/-/releases/v2025.10.1", new PostAuthor("Dana Whitfield", "https://gitlab.example.com/dwhitfield"), Utc(2025, 10, 20, 18, 15, 33), null),
        new("tag:gitlab.example.com,2025:acme/payments-service/commit/7b2e4f0a9c31", "Commit 7b2e4f0a9c31 — Add circuit breaker around the PSP client",
            null,
            "https://gitlab.example.com/acme/payments-service/-/commit/7b2e4f0a9c31", new PostAuthor("Omar Haddad", "https://gitlab.example.com/ohaddad"), Utc(2025, 10, 19, 11, 2, 56), null),
    ];

    private static readonly IReadOnlyList<ExpectedPost> ExpectedRss2BrokenItemSurvivors =
    [
        new("https://example.com/blog/compiler-release", "New compiler release & performance notes",
            "The new release cuts build times by 30% & fixes a regression in the optimizer.",   // this fixture's item 1 has no <img> and no changelog line ⇒ ImageUrl null (differs from rss2.xml!)
            "https://example.com/blog/compiler-release", new PostAuthor("Alice Nguyen", null), Utc(2025, 10, 21, 23, 42, 10), null),
        new("https://example.com/blog/rss-validation", "A practical guide to RSS validation",
            @"What <guid isPermaLink=""true""> actually means and how to validate feeds before parsing them.",
            "https://example.com/blog/rss-validation", new PostAuthor("Alice Nguyen", null), Utc(2025, 10, 21, 16, 15, 44), null),
        new("https://example.com/blog/community-call-dedup", "Community call: Q&A on feed deduplication",
            "Join us & ask anything about item IDs, GUIDs & at-least-once delivery.",   // no <img> in this fixture ⇒ ImageUrl null (differs from rss2.xml!)
            "https://example.com/blog/community-call-dedup", new PostAuthor("Carol Diaz", null), Utc(2025, 10, 21, 1, 30), null),
    ];

    private static IReadOnlyList<ExpectedPost> ExpectedNoGuidSurvivors(string hashedItemId) =>
    [
        new(hashedItemId, "Hashed item", "An item without guid, id or link.", null, null, Utc(2025, 10, 21, 16, 44), null),   // no guid/id/link ⇒ pinned SHA-256 ItemId
        new("https://example.com/sibling-two", "Healthy sibling two", null, "https://example.com/sibling-two", null, Utc(2025, 10, 21, 16, 45), null),
    ];

    // ── Shared assertion: every field of every post, exact values ───────────────────────────────────────────

    /// <summary>
    /// Asserts the full projection of <paramref name="actual"/> against <paramref name="expected"/> post-by-post in document
    /// order: exact count (no extra, no missing item), Id format + uniqueness within the feed, and every other asserted field
    /// with an exact value (nulls included) — including SourceUrl, which must be exactly the feed's source URL for every post.
    /// Any mismatch fails with a per-field message.
    /// </summary>
    private static void AssertEveryFieldExact(IReadOnlyList<Post> actual, IReadOnlyList<ExpectedPost> expected, string sourceUrl)
    {
        Assert.Equal(expected.Count, actual.Count);   // exact post count — no extra, no missing items

        var seenIds = new HashSet<string>();
        for (var i = 0; i < expected.Count; i++)
        {
            var post = actual[i];
            var exp = expected[i];

            Assert.Matches(IdPattern, post.Id);   // system-assigned GUID string format (decision 1)
            Assert.True(seenIds.Add(post.Id), $"post[{i}]: duplicate Id '{post.Id}' — Ids must be unique within the feed");

            AssertField(exp.ItemId, post.ItemId, i, "ItemId");
            AssertField(exp.Title, post.Title, i, "Title");
            AssertField(exp.Description, post.Description, i, "Description (must be decoded plain text)");
            AssertField(exp.Link, post.Link, i, "Link");
            AssertField(exp.Author, post.Author, i, "Author (record equality: Name + Url)");

            AssertField(exp.PublishedAt, post.PublishedAt, i, "PublishedAt (exact zero-offset UTC)");
            Assert.True(post.PublishedAt.Offset == TimeSpan.Zero, $"post[{i}]: PublishedAt offset must be exactly zero: {post.PublishedAt:O}");

            AssertField(exp.ImageUrl, post.ImageUrl, i, "ImageUrl");
            AssertField(sourceUrl, post.SourceUrl, i, "SourceUrl (must be exactly the feed's source URL)");
            Assert.Empty(post.Tags);   // v1.0: tags are assigned by later pipeline stages — the parser never invents any
        }
    }

    /// <summary>Per-field exact comparison with a clear diff message on failure (xUnit v2's Assert.Equal has no message overload).</summary>
    private static void AssertField<T>(T expectedValue, T actualValue, int index, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(expectedValue, actualValue))
            throw new XunitException($"post[{index}] {field}: expected '{Format(expectedValue)}' but was '{Format(actualValue)}'");
    }

    private static string Format<T>(T value) => value is null ? "null" : value.ToString() ?? "null";

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One pinned expected projection of a post (every asserted field except SourceUrl, which is asserted per run against the exact feed URL).</summary>
    private sealed record ExpectedPost(string ItemId, string Title, string? Description, string? Link, PostAuthor? Author, DateTimeOffset PublishedAt, string? ImageUrl);

    /// <summary>Full platform parser — the real selector + all four strategies (mirrors T-013's wiring).</summary>
    private static (FeedParser Parser, LogCapture Logs) CreatePlatformParser()
    {
        var logs = new LogCapture();
        var dates = new FeedDateParser(logs.For<FeedDateParser>());

        var selector = new PostBuilderSelector(
            new FeedItemMapper(dates),                       // generic — v1.0: no description limit (null)
            new YouTubePostBuilder(dates),
            new RedditPostBuilder(dates),
            new GitLabPostBuilder(dates));

        return (new FeedParser(logs.For<FeedParser>(), selector), logs);
    }

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int ms = 0) =>
        new(new DateTime(y, mo, d, h, mi, s).AddMilliseconds(ms), TimeSpan.Zero);
}
