using NewsQuack.App.Parsing;
using NewsQuack.App.Pipeline;
using NewsQuack.Core;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-041: the dedup scenarios across realistic feed cycles, driven through the REAL parser (<see cref="FeedParser"/> + fixtures / minimal inline XML) —
/// not hand-built <see cref="Post"/>s. That is what pins item-ID stability across re-parses, which is what makes the whole dedup scheme work at runtime:
/// a new item is not skipped; an already-confirmed item is skipped; a crash between send and confirm redelivers (at-least-once); guid/Atom-id/link-based
/// items survive a timestamp rewrite; and no-guid-no-link items get a deterministic hash-fallback ID that dedups across identical cycles — with the
/// documented limitation that rewriting the raw date changes the ID. Delivery is confirmed through <see cref="DedupEngine.ConfirmDeliveryAsync"/> and checked
/// through <see cref="DedupCheckStage.ExecuteAsync"/> over a real <see cref="PostContext"/>, on T-014's existing <see cref="RecordingStateStore"/> fake — the exact
/// surface the pipeline runner (T-024) will wire. The engine + stage are the real types; only the store is faked.
/// </summary>
public class DedupScenarioTests
{
    /// <summary>The fixed injected instant — never a real clock (AGENTS.md UTC rule).</summary>
    private static readonly DateTimeOffset DeliveredAtUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // atom.xml entry #1: its Atom <id> is the stable guid-equivalent — the timestamp-rewritten copy below must still map to this ItemId.
    private const string AtomEntry1Id = "urn:uuid:1a2b3c4d-0001-4f5e-9a8b-7c6d5e4f3a2b";

    // Entry #1's <published>/<updated> date string — it occurs exactly twice in the fixture, both inside that one entry (sanity-pinned below).
    private const string AtomEntry1Date = "2025-10-21T14:30:00Z";
    private const string AtomEntry1DateRewritten = "2025-10-22T18:45:33Z";

    // Minimal inline RSS — one item WITHOUT <guid> and WITHOUT <link>: the hash-fallback path. Title + date are present, so it is a valid item.
    private const string HashFallbackRss = """
        <rss version="2.0"><channel><title>Minimal</title>
          <item><title>Orphan item</title><pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item>
        </channel></rss>
        """;

    // The same item with ONLY the date string rewritten — the raw date is part of the hash input (the documented limitation).
    private const string HashFallbackRssDateRewritten = """
        <rss version="2.0"><channel><title>Minimal</title>
          <item><title>Orphan item</title><pubDate>Wed, 22 Oct 2025 17:42:10 -0700</pubDate></item>
        </channel></rss>
        """;

    [Fact]
    public async Task NewItem_FirstCycle_NotSkipped()
    {
        var (parser, _, stage, _) = CreatePipeline();

        var result = await parser.ParseAsync(FixtureLoader.Load("rss2.xml"), DedupTestPost.FeedUrl);

        Assert.Null(result.FeedError);
        Assert.NotEmpty(result.Posts);

        var ctx = await CheckAsync(stage, result.Posts[0]);   // first item of the feed — never seen by this instance

        Assert.False(ctx.Skip);   // new item → flows on to enrichment + send
    }

    [Fact]
    public async Task AlreadySent_SecondCycle_Skipped()
    {
        var (parser, engine, stage, store) = CreatePipeline();
        var xml = FixtureLoader.Load("rss2.xml");

        // Cycle 1: the item is parsed and "sent"; the target answers delivered → confirm — the ONLY moment delivery becomes persistent.
        var first = await parser.ParseAsync(xml, DedupTestPost.FeedUrl);
        Assert.Null(first.FeedError);
        var postA = first.Posts[0];

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, postA.ItemId, DeliveredAtUtc, null);
        Assert.Single(store.MarkDeliveredCalls);   // exactly one confirm — the atomic mark happened

        // Cycle 2 (next poll): the feed still serves the item, so it is re-parsed.
        var second = await parser.ParseAsync(xml, DedupTestPost.FeedUrl);
        var postB = second.Posts[0];

        Assert.Equal(postA.ItemId, postB.ItemId);   // stability pin: identical XML ⇒ identical ItemId across re-parses

        var ctx = await CheckAsync(stage, postB);

        Assert.True(ctx.Skip);   // already delivered to this instance → no second send
    }

    [Fact]
    public async Task CrashBetweenSendAndConfirm_SecondCycle_Redelivered()
    {
        var (parser, _, stage, store) = CreatePipeline();
        var xml = FixtureLoader.Load("rss2.xml");

        // Cycle 1: the item is parsed and "sent" — then the process crashes BEFORE ConfirmDeliveryAsync. No confirm call at all.
        var first = await parser.ParseAsync(xml, DedupTestPost.FeedUrl);
        Assert.Null(first.FeedError);
        var postA = first.Posts[0];

        Assert.Empty(store.MarkDeliveredCalls);   // the crash: delivery was in flight, not yet persistent

        // Cycle 2 (after restart): the feed still serves the item, so it is re-parsed. Unconfirmed ⇒ NOT skipped.
        var second = await parser.ParseAsync(xml, DedupTestPost.FeedUrl);
        Assert.Equal(postA.ItemId, second.Posts[0].ItemId);

        var ctx = await CheckAsync(stage, second.Posts[0]);

        Assert.False(ctx.Skip);   // at-least-once: the crash cost a duplicate, never a loss
    }

    [Fact]
    public async Task TimestampRewrite_GuidOrLinkItem_StillSkippedAfterConfirm()
    {
        var (parser, engine, stage, _) = CreatePipeline();
        var xml = FixtureLoader.Load("atom.xml");

        // Sanity pin: the date string we rewrite belongs to exactly ONE entry (its <published> and <updated>) — otherwise we would be testing a different item.
        Assert.Equal(2, CountOccurrences(xml, AtomEntry1Date));

        // Cycle 1: parse the original fixture; confirm delivery of the guid-based entry.
        var first = await parser.ParseAsync(xml, DedupTestPost.FeedUrl);
        Assert.Null(first.FeedError);
        var postA = Assert.Single(first.Posts, p => p.ItemId == AtomEntry1Id);

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, postA.ItemId, DeliveredAtUtc, null);

        // Cycle 2: the feed rewrites ONLY that entry's timestamp — same item, new date string.
        var rewrittenXml = xml.Replace(AtomEntry1Date, AtomEntry1DateRewritten);
        var second = await parser.ParseAsync(rewrittenXml, DedupTestPost.FeedUrl);
        Assert.Null(second.FeedError);

        // Same ItemId despite the rewrite (the Atom <id> is the guid-equivalent) — and the parsed date actually moved, so this is not an unchanged item.
        var postB = Assert.Single(second.Posts, p => p.ItemId == AtomEntry1Id);
        Assert.NotEqual(postA.PublishedAt, postB.PublishedAt);

        var ctx = await CheckAsync(stage, postB);

        Assert.True(ctx.Skip);   // timestamp rewrites do not cause redelivery for guid/link-based items
    }

    [Fact]
    public async Task HashFallback_NoGuidNoLink_StableIdAcrossCycles_SkippedAfterConfirm()
    {
        var (parser, engine, stage, _) = CreatePipeline();

        // Cycle 1 and cycle 2 parse IDENTICAL content — the deterministic hash-fallback ID must be stable across re-parses.
        var first = await parser.ParseAsync(HashFallbackRss, DedupTestPost.FeedUrl);
        Assert.Null(first.FeedError);
        var postA = Assert.Single(first.Posts);

        var second = await parser.ParseAsync(HashFallbackRss, DedupTestPost.FeedUrl);
        var postB = Assert.Single(second.Posts);

        Assert.Equal(postA.ItemId, postB.ItemId);           // stable across identical cycles…
        Assert.Matches("^[0-9a-f]{64}$", postA.ItemId);     // …and it is the SHA-256 form (no guid/link to fall back on)

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, postA.ItemId, DeliveredAtUtc, null);

        var ctx = await CheckAsync(stage, postB);

        Assert.True(ctx.Skip);   // hash-fallback items dedupe across identical cycles
    }

    [Fact]
    public async Task HashFallback_TimestampRewrite_Redelivered_DocumentedLimitation()
    {
        var (parser, engine, stage, _) = CreatePipeline();

        // Cycle 1: parse the original item; confirm delivery under its hash-fallback ID.
        var first = await parser.ParseAsync(HashFallbackRss, DedupTestPost.FeedUrl);
        Assert.Null(first.FeedError);
        var postA = Assert.Single(first.Posts);

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, postA.ItemId, DeliveredAtUtc, null);

        // Cycle 2: the feed rewrites the item's date string. The raw date is part of the hash input ⇒ a DIFFERENT ItemId —
        // the documented limitation (at-least-once tolerates the redelivery; pinned as expected behavior, not a defect).
        var second = await parser.ParseAsync(HashFallbackRssDateRewritten, DedupTestPost.FeedUrl);
        Assert.Null(second.FeedError);
        var postB = Assert.Single(second.Posts);

        Assert.NotEqual(postA.ItemId, postB.ItemId);   // rewritten raw date ⇒ new hash-fallback ID

        var ctx = await CheckAsync(stage, postB);

        Assert.False(ctx.Skip);   // redelivered — the limitation is pinned, not a failure
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private static (FeedParser Parser, DedupEngine Engine, DedupCheckStage Stage, RecordingStateStore Store) CreatePipeline()
    {
        var store = new RecordingStateStore();
        var logs = new LogCapture();

        // One stateless engine serves BOTH the confirm and the check stage — exactly how production wires it (T-014).
        var engine = new DedupEngine(store, logs.For<DedupEngine>());

        return (
            new FeedParser(logs.For<FeedParser>(), new FeedItemMapper(new FeedDateParser(logs.For<FeedDateParser>()))),
            engine,
            new DedupCheckStage(engine, logs.For<DedupCheckStage>()),
            store);
    }

    /// <summary>Runs the dedup check over a real PostContext built from one parsed post — exactly what the pipeline runner builds for one item.</summary>
    private static async Task<PostContext> CheckAsync(DedupCheckStage stage, Post post)
    {
        var ctx = new PostContext { InstanceId = DedupTestPost.InstanceId, CurrentPost = post };

        await stage.ExecuteAsync(ctx);

        return ctx;
    }

    /// <summary>How many times the needle occurs in the haystack (ordinal) — used to pin that a fixture rewrite touches one item only.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
