using System.Globalization;
using NewsQuack.App.Parsing;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // LogLevel for the pass-2 fallback assertion
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-012: end-to-end tests of the two-pass <see cref="FeedParser"/> against the shared fixtures, plus the ItemId/link
/// derivation rules and a culture-hostility guard. Expected UTC values were verified empirically against CodeHollow 1.2.6
/// on net10 (coordinator decision 2) and are asserted exactly.
/// </summary>
public class FeedParserTests
{
    private const string Source = "https://example.com/feed.xml";

    [Fact]
    public async Task Parse_Rss2Fixture_AllItemsMappedWithExactUtcDates()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("rss2.xml"), Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        Assert.Equal(4, result.Posts.Count);

        // Exact UTC instants — note item 3 stays on Oct 21 (local 00:05 at -0700) and item 4 crosses midnight into Oct 21.
        Assert.Equal(
            [Utc(2025, 10, 21, 23, 42, 10), Utc(2025, 10, 21, 16, 15, 44), Utc(2025, 10, 21, 7, 5, 12), Utc(2025, 10, 21, 1, 30, 0)],
            result.Posts.Select(p => p.PublishedAt).ToList());

        // Authors come from dc:creator: name only, url always null for RSS.
        Assert.Equal(["Alice Nguyen", "Alice Nguyen", "Bob Fischer", "Carol Diaz"], result.Posts.Select(p => p.Author?.Name).ToList());
        Assert.All(result.Posts, p => Assert.Null(p.Author?.Url));

        // ImageUrl: first <img src> in the raw description HTML (items 1 & 4 have one; 2 & 3 do not).
        Assert.Equal(
            ["https://example.com/img/compiler-chart.png", null, null, "https://example.com/img/community-call.png"],
            result.Posts.Select(p => p.ImageUrl).ToList());
    }

    [Fact]
    public async Task Parse_AtomFixture_OffsetsNormalizedToUtc()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("atom.xml"), Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        Assert.Equal(4, result.Posts.Count);

        // Z and +02:00 forms both normalize to zero-offset UTC.
        Assert.Equal(
            [Utc(2025, 10, 21, 14, 30, 0), Utc(2025, 10, 21, 11, 58, 12), Utc(2025, 10, 20, 9, 15, 44), Utc(2025, 10, 19, 21, 58, 7)],
            result.Posts.Select(p => p.PublishedAt).ToList());

        // Entries with their own <author> keep name+uri; entries without inherit the feed-level author (name+uri).
        Assert.Equal(
            [("Maria Weber", "https://example.org/maria"), ("Peter Novak", "https://example.org/peter"),
             ("Maria Weber", "https://example.org/maria"), ("Maria Weber", "https://example.org/maria")],
            result.Posts.Select(p => (p.Author?.Name, p.Author?.Url)).ToList());
    }

    [Fact]
    public async Task Parse_YouTubeFixture_ItemsAndDatesMapped()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("youtube.xml"), Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        Assert.Equal(3, result.Posts.Count);

        Assert.Equal(
            [Utc(2025, 10, 21, 16, 42, 10), Utc(2025, 10, 19, 18, 5, 33), Utc(2025, 10, 17, 12, 30, 0)],
            result.Posts.Select(p => p.PublishedAt).ToList());

        // ItemId is the tag:youtube.com guid value verbatim.
        Assert.Equal(
            ["tag:youtube.com,2016:video:aB3dE5fGh7i", "tag:youtube.com,2016:video:jK9mL2nOp4q", "tag:youtube.com,2016:video:rSt6uVwXyZ8"],
            result.Posts.Select(p => p.ItemId).ToList());
    }

    [Fact]
    public async Task Parse_RedditFixture_FractionalSecondsPreserved()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("reddit.xml"), Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        Assert.Equal(3, result.Posts.Count);

        // Millisecond precision is preserved end-to-end.
        Assert.Equal(
            [Utc(2025, 10, 21, 16, 39, 58, ms: 456), Utc(2025, 10, 21, 14, 12, 47, ms: 901), Utc(2025, 10, 20, 21, 45, 19, ms: 664)],
            result.Posts.Select(p => p.PublishedAt).ToList());

        // Authors are /u/name with no url.
        Assert.Equal(["/u/crate_roller", "/u/borrow_checker_fan", "/u/migrating_from_cpp"], result.Posts.Select(p => p.Author?.Name).ToList());
        Assert.All(result.Posts, p => Assert.Null(p.Author?.Url));
    }

    [Fact]
    public async Task Parse_GitlabFixture_ItemsAndDatesMapped()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("gitlab.xml"), Source);

        Assert.Null(result.FeedError);
        Assert.Equal(0, result.ItemErrorCount);
        Assert.Equal(3, result.Posts.Count);

        Assert.Equal(
            [Utc(2025, 10, 21, 16, 40, 7), Utc(2025, 10, 20, 18, 15, 33), Utc(2025, 10, 19, 11, 2, 56)],
            result.Posts.Select(p => p.PublishedAt).ToList());

        Assert.Equal(["Dana Whitfield", "Dana Whitfield", "Omar Haddad"], result.Posts.Select(p => p.Author?.Name).ToList());
    }

    // ── Broken feeds ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_Rss2BrokenItem_BrokenItemDroppedOthersSurvive()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("rss2-broken-item.xml"), Source);

        Assert.Null(result.FeedError);              // the defect is per-item, not whole-feed
        Assert.Equal(1, result.ItemErrorCount);     // exactly the title-less item
        Assert.Equal(3, result.Posts.Count);

        // Only the title-less item (link .../item-without-title) is gone; the other three survive.
        var links = result.Posts.Select(p => p.Link).ToList();
        Assert.DoesNotContain("https://example.com/blog/item-without-title", links);
        Assert.Contains("https://example.com/blog/compiler-release", links);
        Assert.Contains("https://example.com/blog/rss-validation", links);
        Assert.Contains("https://example.com/blog/community-call-dedup", links);
    }

    [Fact]
    public async Task Parse_AtomBrokenDate_FallsBackToTolerantPass_DropsOnlyBrokenEntry()
    {
        var (parser, logs) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("atom-broken-date.xml"), Source);

        // Pass 1 throws on this fixture (unparseable <updated>, no <published>), so the tolerant pass must have run.
        Assert.NotNull(logs.Find(LogLevel.Information, "tolerant fallback"));

        Assert.Null(result.FeedError);              // per-item defect, not whole-feed
        Assert.Equal(1, result.ItemErrorCount);     // exactly the broken entry
        Assert.Equal(2, result.Posts.Count);

        // The two healthy entries keep their exact UTC dates; the broken one is dropped (never stamped "now").
        Assert.Equal([Utc(2025, 10, 21, 14, 30, 0), Utc(2025, 10, 21, 11, 58, 12)], result.Posts.Select(p => p.PublishedAt).ToList());
    }

    [Fact]
    public async Task Parse_MalformedFeed_ReturnsFeedErrorNotSilentEmptyList()
    {
        var (parser, _) = CreateParser();

        var result = await parser.ParseAsync(FixtureLoader.Load("feed-malformed.xml"), Source);

        Assert.Empty(result.Posts);
        Assert.NotNull(result.FeedError);
        Assert.Contains("XmlException", result.FeedError!);   // carries the exception type name — observable, not silent
    }

    [Fact]
    public async Task Parse_ValidXmlWithZeroItems_ReturnsFeedError()
    {
        var (parser, _) = CreateParser();

        const string emptyFeed = "<rss version=\"2.0\"><channel><title>Empty</title></channel></rss>";
        var result = await parser.ParseAsync(emptyFeed, Source);

        Assert.Empty(result.Posts);
        Assert.Equal("no items found", result.FeedError);   // well-formed but item-less ⇒ observable error, never a silent []
    }

    // ── Culture hostility (smoke guard) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_AtomFixture_UnderDeDeCulture_SameResultAsInvariant()
    {
        var xml = FixtureLoader.Load("atom.xml");

        var (invariantParser, _) = CreateParser();
        var invariant = await invariantParser.ParseAsync(xml, Source);

        var original = CultureInfo.DefaultThreadCurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");
        try
        {
            var (deDeParser, _) = CreateParser();
            var underDeDe = await deDeParser.ParseAsync(xml, Source);

            Assert.Equal(Key(invariant.Posts), Key(underDeDe.Posts));   // identical output regardless of thread culture
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = original;         // restore so we never leak culture into other tests
        }
    }

    // ── ItemId & link derivation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ItemId_GuidPresent_UsedVerbatim() =>
        Assert.Equal("tag:youtube.com,2016:video:abc", FeedItemMapper.DeriveItemId(
            "tag:youtube.com,2016:video:abc", "https://example.com/abs", Source, "Title", "2025-10-21"));

    [Fact]
    public void ItemId_NoGuid_AbsoluteLinkUsed()
    {
        Assert.Equal("https://example.com/post/42", FeedItemMapper.DeriveItemId(null, "https://example.com/post/42", Source, "Title", "2025-10-21"));
        Assert.Equal("https://example.com/post/42", FeedItemMapper.DeriveItemId("   ", "https://example.com/post/42", Source, "Title", "2025-10-21"));   // blank guid ⇒ falls through
    }

    [Fact]
    public void ItemId_NoGuidNoLink_StableSha256HexIsDeterministic()
    {
        var first = FeedItemMapper.DeriveItemId(null, null, Source, "Some Title", "2025-10-21T14:30:00Z");
        var second = FeedItemMapper.DeriveItemId(null, null, Source, "Some Title", "2025-10-21T14:30:00Z");

        Assert.Equal(first, second);                       // same inputs ⇒ same id (stable)
        Assert.Matches("^[0-9a-f]{64}$", first!);          // 64 lowercase-hex chars = SHA-256
    }

    [Fact]
    public async Task Parse_RelativeLink_ResolvedAgainstSourceUrl()
    {
        var (parser, _) = CreateParser();

        const string feed = """
            <rss version="2.0"><channel><title>Rel</title>
              <item><title>Relative link</title><link>/posts/42</link><guid isPermaLink="false">g1</guid>
                <pubDate>Tue, 21 Oct 2025 16:42:10 +0000</pubDate></item>
            </channel></rss>
            """;

        var result = await parser.ParseAsync(feed, "https://example.com/feed.xml");

        Assert.Single(result.Posts);
        Assert.Equal("https://example.com/posts/42", result.Posts[0].Link);   // relative resolved against the feed URL
    }

    // ── Two-pass equivalence ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parse_WellFormedFeed_TolerantPassMatchesPrimaryPass()
    {
        var xml = FixtureLoader.Load("atom.xml");
        var (primary, _) = CreateParser();

        // Primary pass (CodeHollow) and the tolerant pass must produce the same canonical posts for the same items.
        var viaPrimary = await primary.ParseAsync(xml, Source);
        var viaTolerant = primary.ParseTolerantPass(xml, Source);

        Assert.Equal(Key(viaPrimary.Posts), Key(viaTolerant.Posts));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private static (FeedParser Parser, LogCapture Logs) CreateParser()
    {
        var logs = new LogCapture();
        var parser = new FeedParser(logs.For<FeedParser>(), new FeedItemMapper(new FeedDateParser(logs.For<FeedDateParser>())));
        return (parser, logs);
    }

    // Comparable projection of a post — excludes the random per-construction Id, which legitimately differs between runs.
    private static List<(string ItemId, string Title, DateTimeOffset At, string? Link, string? AuthorName, string? AuthorUrl)> Key(IReadOnlyList<Post> posts) =>
        posts.Select(p => (p.ItemId, p.Title, p.PublishedAt, p.Link, p.Author?.Name, p.Author?.Url)).ToList();

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int ms = 0) =>
        new(new DateTime(y, mo, d, h, mi, s).AddMilliseconds(ms), TimeSpan.Zero);
}
