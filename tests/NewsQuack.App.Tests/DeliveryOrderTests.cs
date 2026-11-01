using System.Diagnostics;   // Stopwatch for the drain timeout (not a date — no UTC rule involved)
using NewsQuack.App.Parsing;        // FeedParser / FeedItemMapper / FeedDateParser — the guard test builds the REAL parser directly (internal ctor via InternalsVisibleTo)
using NewsQuack.App.Pipeline;       // BoundedSendQueue — read back the posts SendStage enqueued (internal TryRead via InternalsVisibleTo)
using NewsQuack.Core;               // Post
using Microsoft.Extensions.Logging; // LogLevel for log assertions
using WireMock.RequestBuilders;     // Request.Create() for stubbing feeds
using WireMock.ResponseBuilders;    // Response.Create() for stub responses
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-047 (issue #55): a fresh-DB backlog must be delivered OLDEST-FIRST. The scheduler sorts each feed's parsed posts in stable ascending
/// PublishedAt order at the pipeline entry — so after catch-up finishes, the LAST message enqueued is the newest item. Reuses
/// SchedulerTestHarness (real two-pass parser + real v1.0 stages over a per-instance BoundedSendQueue) and InstanceSchedulerTests' queue-drain
/// pattern. The guard test pins that FeedParser still returns document order — the sort lives in the scheduler, not the parser.
/// </summary>
public class DeliveryOrderTests
{
    private const string InstanceId = "inst-a";

    /// <summary>A three-item backlog listed NEWEST-FIRST (the RSS/Atom document-order convention): newest → middle → oldest.</summary>
    private const string RssBacklogNewestFirst = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>Backlog</title>" +
        "<item><title>Newest post</title><link>https://example.com/newest</link><guid isPermaLink=\"true\">https://example.com/newest</guid>" +
        "<pubDate>Wed, 22 Oct 2025 16:44:10 -0700</pubDate></item>" +
        "<item><title>Middle post</title><link>https://example.com/middle</link><guid isPermaLink=\"true\">https://example.com/middle</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:43:10 -0700</pubDate></item>" +
        "<item><title>Oldest post</title><link>https://example.com/oldest</link><guid isPermaLink=\"true\">https://example.com/oldest</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item></channel></rss>";

    /// <summary>Two items with BYTE-IDENTICAL dates — the stable sort must keep their document order (alpha before beta).</summary>
    private const string RssIdenticalDates = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>Ties</title>" +
        "<item><title>Alpha post</title><link>https://example.com/alpha</link><guid isPermaLink=\"true\">https://example.com/alpha</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item>" +
        "<item><title>Beta post</title><link>https://example.com/beta</link><guid isPermaLink=\"true\">https://example.com/beta</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item></channel></rss>";

    [Fact]
    public async Task Delivery_BacklogListedNewestFirstInXml_PostsEnqueuedOldestFirst()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(300), ["/feed.xml"]);

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssBacklogNewestFirst));   // document order is newest-first (RSS convention)

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try
        {
            var queue = (BoundedSendQueue)harness.Registry.For(InstanceId);   // the SAME queue SendStage enqueues into
            var collected = await DrainAsync(queue, 3);                       // one feed ⇒ all three come from its first cycle, in pipeline-entry order

            Assert.Equal(3, collected.Count);
            Assert.Equal(["https://example.com/oldest", "https://example.com/middle", "https://example.com/newest"],   // oldest FIRST (issue #55)
                collected.Select(p => p.ItemId).ToList());

            for (var i = 1; i < collected.Count; i++)   // the enqueued sequence is ascending PublishedAt — after catch-up, the LAST message is the newest item
                Assert.True(collected[i - 1].PublishedAt <= collected[i].PublishedAt, $"enqueued item {i} must not predate item {i - 1}");
        }
        finally
        {
            cts.Cancel();   // clean shutdown — a healthy run leaves no Error logs (per-cycle exceptions would log here)
            await scheduler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Delivery_MiddleItemAlreadyDelivered_OnlyNewItemsEnqueuedOldestFirst()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(300), ["/feed.xml"]);

        harness.Store.SeedDelivered(InstanceId, "https://example.com/middle");   // confirmed in an earlier cycle — dedup must skip it

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssBacklogNewestFirst));

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try
        {
            var queue = (BoundedSendQueue)harness.Registry.For(InstanceId);
            var collected = await DrainAsync(queue, 2);   // only the two NEW items are enqueued — the middle one is skipped by stage 10

            Assert.Equal(["https://example.com/oldest", "https://example.com/newest"],   // reordering changed WHICH sequence is enqueued, not WHICH items are skipped
                collected.Select(p => p.ItemId).ToList());
        }
        finally
        {
            cts.Cancel();
            await scheduler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Delivery_TwoItemsWithIdenticalTimestamps_DocumentOrderPreserved()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(300), ["/feed.xml"]);

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssIdenticalDates));   // byte-identical pubDate on both items

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try
        {
            var queue = (BoundedSendQueue)harness.Registry.For(InstanceId);
            var collected = await DrainAsync(queue, 2);

            Assert.Equal(["https://example.com/alpha", "https://example.com/beta"],   // LINQ OrderBy is stable ⇒ identical timestamps keep document order
                collected.Select(p => p.ItemId).ToList());
        }
        finally
        {
            cts.Cancel();
            await scheduler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Parser_BacklogListedNewestFirst_ReturnsDocumentOrderUnchanged()
    {
        // Guard: the parser output stays byte-identical (document order) — the oldest-first sort lives in the scheduler, not here. A future
        // "convenience sort" migrating into FeedParser would change the frozen IFeedParser contract's observable output and fail this test.
        var logs = new LogCapture();
        var parser = new FeedParser(logs.For<FeedParser>(), new FeedItemMapper(new FeedDateParser(logs.For<FeedDateParser>()), null));

        var result = await parser.ParseAsync(RssBacklogNewestFirst, "https://example.com/feed.xml");

        Assert.Null(result.FeedError);
        Assert.Equal(["https://example.com/newest", "https://example.com/middle", "https://example.com/oldest"],   // document order — newest first
            result.Posts.Select(p => p.ItemId).ToList());
    }

    /// <summary>Non-blocking drain of the instance's queue until <paramref name="count"/> posts are collected (InstanceSchedulerTests' pattern) —
    /// posts arrive as the cycle parses them; a 5 s bound keeps a failure loud instead of hanging.</summary>
    private static async Task<List<Post>> DrainAsync(BoundedSendQueue queue, int count)
    {
        var collected = new List<Post>();
        var sw = Stopwatch.StartNew();

        while (collected.Count < count && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (queue.TryRead(out var post)) collected.Add(post);   // non-blocking drain — never waits on the queue itself
            else await Task.Delay(20, CancellationToken.None);
        }

        return collected;
    }
}
