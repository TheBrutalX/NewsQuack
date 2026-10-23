using System.Diagnostics;   // Stopwatch for the timing test (not a date — no UTC rule involved)
using NewsQuack.App.Pipeline;        // BoundedSendQueue — read back the posts SendStage enqueued (internal ReadAsync via InternalsVisibleTo)
using NewsQuack.Core;                // Post / IPostStage
using Microsoft.Extensions.Logging;   // LogLevel for log assertions
using WireMock.RequestBuilders;      // Request.Create() for stubbing feeds + robots.txt
using WireMock.ResponseBuilders;     // Response.Create() for stub responses
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-024 integration: the per-instance scheduler against REAL production pieces (WireMock-backed FeedFetcher, two-pass FeedParser, FeedErrorTracker,
/// and the full v1.0 stage chain over a per-instance BoundedSendQueue) via SchedulerTestHarness. Pins the binding scheduling semantics (coordinator
/// decision 6): immediate first cycle + absolute slots with no drift, fetch→parse→pipeline delivery of posts, failure ⇒ backoff skip, auto-removed ⇒
/// skip without fetching, RobotsBlocked ⇒ NOT a failure, and clean cancellation with no error logs. Small pollIntervals; generous timing bounds (no flakes).
/// </summary>
public class InstanceSchedulerTests
{
    private const string InstanceId = "inst-a";

    /// <summary>A healthy one-item RSS — parses to exactly one post (used where the test only needs a successful fetch).</summary>
    private const string RssOneItem = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>t</title>" +
        "<item><title>Only post</title><link>https://example.com/only</link><guid isPermaLink=\"true\">https://example.com/only</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item></channel></rss>";

    /// <summary>A real RSS with two items — each must flow through dedup → image (fallback) → send into the instance's queue.</summary>
    private const string RssTwoItems = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>Two posts</title>" +
        "<item><title>First post</title><link>https://example.com/first</link><guid isPermaLink=\"true\">https://example.com/first</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item>" +
        "<item><title>Second post</title><link>https://example.com/second</link><guid isPermaLink=\"true\">https://example.com/second</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:43:10 -0700</pubDate></item></channel></rss>";

    [Fact]
    public async Task Scheduler_FirstCycleRunsImmediately_SecondOnlyAfterInterval()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(500), ["/feed.xml"]);

        // A minimal valid RSS (one item) — the fetch is Ok; we only observe WHEN the feed is requested, not what parses.
        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssOneItem));

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);   // the loop runs with cts.Token as its stopping token

        try
        {
            var sw = Stopwatch.StartNew();

            // First cycle must run IMMEDIATELY at startup — well before the 500 ms interval.
            while (harness.RequestCount("/feed.xml") < 1 && sw.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20, CancellationToken.None);
            var firstSeen = sw.Elapsed;

            // Second cycle must wait for the full interval — absolute slots, no drift (never re-anchored to "now").
            while (harness.RequestCount("/feed.xml") < 2 && sw.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20, CancellationToken.None);
            var secondSeen = sw.Elapsed;

            Assert.True(firstSeen < TimeSpan.FromMilliseconds(300), $"first cycle should run immediately at startup, but took {firstSeen}");
            Assert.True(secondSeen >= TimeSpan.FromMilliseconds(425), $"second cycle should wait for the 500 ms interval (no drift), but came at {secondSeen}");
        }
        finally
        {
            cts.Cancel();   // clean shutdown — a healthy run leaves no Error logs (per-cycle exceptions would log here, decision 6)
            await scheduler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Scheduler_SuccessfulFeed_PostsReachSendQueueThroughRealStages()
    {
        const string fallback = "https://img.example/fallback.png";   // configured so the image stage's effect is observable on the delivered posts
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(300), ["/feed.xml"], fallbackImageUrl: fallback);

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssTwoItems));   // two items — both must be delivered

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try
        {
            var queue = (BoundedSendQueue)harness.Registry.For(InstanceId);   // the SAME queue SendStage enqueues into
            var collected = new List<Post>();
            var sw = Stopwatch.StartNew();

            while (collected.Count < 2 && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (queue.TryRead(out var post)) collected.Add(post);   // non-blocking drain — posts arrive as the cycle parses them
                else await Task.Delay(20, CancellationToken.None);
            }

            Assert.Equal(2, collected.Count);   // BOTH posts reached the queue...
            Assert.Contains(collected, p => p.ItemId == "https://example.com/first");    // ...item 1 (dedup did not skip it — the store was empty)...
            Assert.Contains(collected, p => p.ItemId == "https://example.com/second");   // ...and item 2...
            Assert.All(collected, p => Assert.Equal(fallback, p.ImageUrl));              // ...each with the image stage's fallback applied (proves all three real stages ran)
        }
        finally
        {
            cts.Cancel();
            await scheduler.StopAsync(CancellationToken.None);   // clean shutdown — a success path leaves no Error logs anywhere in the cycle
        }

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Scheduler_FeedFailure_RecordsErrorAndSkipsNextCycleWhileInBackoff()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(200), ["/feed.xml"]);   // default threshold 10 — no auto-remove in this window

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(503));   // a non-cascading 5xx ⇒ HttpError (a failure)

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try { await Task.Delay(700, CancellationToken.None); }   // enough for ~3-4 cycles at 200 ms — only the first may fetch
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }

        var feedUrl = harness.FeedUrl("/feed.xml");
        Assert.Equal(1, harness.Store.GetRow(feedUrl)!.ErrorCount);   // exactly ONE failure recorded (decision 7: subsequent cycles skip while in backoff)...
        Assert.Equal(1, harness.RequestCount("/feed.xml"));           // ...so the feed was fetched only once — the 30 s backoff suppressed every later cycle
    }

    [Fact]
    public async Task Scheduler_AutoRemovedFeed_NotFetchedAgain()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(200), ["/feed.xml"], autoRemoveThreshold: 1);   // removed on the FIRST failure

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(503));   // one failure is enough to cross the threshold of 1

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try { await Task.Delay(700, CancellationToken.None); }   // several cycles — every one after the first must skip WITHOUT fetching
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }

        var feedUrl = harness.FeedUrl("/feed.xml");
        Assert.True(harness.Tracker.IsAutoRemoved(feedUrl));      // auto-removed at the first failure (threshold 1)...
        Assert.Equal(1, harness.RequestCount("/feed.xml"));       // ...and auto-removed feeds are skipped WITHOUT fetching (decision 6) — fetched exactly once
    }

    [Fact]
    public async Task Scheduler_RobotsBlocked_DoesNotRecordFailure()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(200), ["/private/feed.xml"]);

        // robots.txt disallows the feed's path — the fetcher returns RobotsBlocked WITHOUT fetching the feed itself.
        harness.Server.Given(Request.Create().WithPath("/robots.txt"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("User-agent: *\nDisallow: /private/"));

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try { await Task.Delay(500, CancellationToken.None); }   // a couple cycles — each returns RobotsBlocked (site policy, not feed health)
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }

        var feedUrl = harness.FeedUrl("/private/feed.xml");
        Assert.Null(harness.Store.GetRow(feedUrl));              // RobotsBlocked ⇒ NO tracker call ⇒ no persisted error state (decision 6)...
        Assert.Equal(0, harness.RequestCount("/private/feed.xml"));   // ...the feed itself was never fetched (the robots gate blocks first)...
        Assert.Equal(1, harness.RequestCount("/robots.txt"));     // ...and the block came from robots.txt (consulted once, then cached per origin)
    }

    [Fact]
    public async Task Scheduler_Cancellation_StopsLoopCleanlyWithoutErrorLogs()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(200), ["/feed.xml"]);

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssOneItem));   // a healthy feed — successes only, so no failure errors exist

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try { await Task.Delay(500, CancellationToken.None); }   // let it run a couple of successful cycles...
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }   // ...then cancel — the loop must stop cleanly (OCE is not an error)

        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));   // clean cancellation on a healthy feed ⇒ no Error logs (the shutdown path owns OCE)
    }
}
