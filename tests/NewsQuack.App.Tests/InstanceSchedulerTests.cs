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

    /// <summary>Settle window before exact-count assertions on suppression/auto-removal: lets ≥1 later cycle actually run so the count proves the behavior, not "nothing has happened yet".</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);   // 2× the 200 ms poll interval

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

    /// <summary>House-style deadline poll: ~20 ms cadence until the condition holds or the deadline passes. On timeout, fails stating the expected
    /// condition AND what was actually observed (counts/state) at that moment — never a bare "timed out". Replaces fixed Task.Delay guesses.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan deadline, Func<string> observedState)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < deadline) await Task.Delay(20, CancellationToken.None);

        Assert.True(condition(), $"Timed out after {deadline} waiting for the expected state. Observed: {observedState()}");
    }

    [Fact]
    public async Task Scheduler_FirstCycleRunsImmediately_SecondOnlyAfterInterval()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(2000), ["/feed.xml"]);   // 2000 ms interval — scaled so the budgets carry CI headroom (see below)

        // A minimal valid RSS (one item) — the fetch is Ok; we only observe WHEN the feed is requested, not what parses.
        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssOneItem));

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);   // the loop runs with cts.Token as its stopping token

        try
        {
            var sw = Stopwatch.StartNew();   // one shared stopwatch — both waits are measured from StartAsync, so the ~7 s TOTAL deadline below is meaningful

            // First cycle must run IMMEDIATELY at startup. The 4 s poll deadline carries ≈ ~340 % headroom over the observed ~905 ms worst
            // first-request latency on a loaded CI runner (CI run 40418934329 measured 905.6 ms against the old 300 ms budget) — it guarantees
            // MEASUREMENT, not a hang; immediacy itself is pinned by the 1500 ms budget below.
            await WaitUntilAsync(
                () => harness.RequestCount("/feed.xml") >= 1,
                TimeSpan.FromSeconds(4),
                () => $"first /feed.xml request — RequestCount={harness.RequestCount("/feed.xml")}");
            var firstSeen = sw.Elapsed;

            // Second cycle must wait for the full interval — absolute slots, no drift (never re-anchored to "now"). The deadline is the REMAINING
            // time of a shared ~7 s TOTAL budget: it covers both regimes — normal slot at t=2000 (arrives ≈ 2.1 s) and catch-up after a slow
            // startup, where remaining = slot − now goes negative (InstanceScheduler.ExecuteAsync), the delay is skipped, and the second request
            // lands right after cycle 1 (≤ ~4.x s worst). Either way well inside ~7 s total.
            await WaitUntilAsync(
                () => harness.RequestCount("/feed.xml") >= 2,
                TimeSpan.FromSeconds(7) - sw.Elapsed,   // remaining slice of the shared ~7 s TOTAL deadline (the first wait owns its own ~4 s slice)
                () => $"second /feed.xml request — RequestCount={harness.RequestCount("/feed.xml")}");
            var secondSeen = sw.Elapsed;

            Assert.True(firstSeen < TimeSpan.FromMilliseconds(1500), $"first cycle should run immediately at startup, but took {firstSeen}");   // 1500 ms budget: ~65 % headroom over the observed ~905 ms CI worst case AND well before the next slot at t=2000 — proves "immediate", not "caught up"
            Assert.True(secondSeen >= TimeSpan.FromMilliseconds(1875), $"second cycle should wait for the 2000 ms interval (no drift), but came at {secondSeen}");   // 125 ms tolerance absorbs timer granularity around the t=2000 slot; holds in BOTH regimes: normal slot ≈ t=2000, or catch-up after a slow startup (negative remaining ⇒ delay skipped ⇒ second cycle lands ≥ ~2000 ms)
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

        var feedUrl = harness.FeedUrl("/feed.xml");

        try
        {
            // Poll until the first cycle has fetched AND recorded its failure (recording lands milliseconds after the local WireMock response).
            // 3 s deadline ≈ ~230 % headroom over the observed ~905 ms worst first-request latency on a loaded CI runner.
            await WaitUntilAsync(
                () => harness.RequestCount("/feed.xml") >= 1 && harness.Store.GetRow(feedUrl)?.ErrorCount == 1,
                TimeSpan.FromSeconds(3),
                () => $"first cycle fetched and recorded its failure — RequestCount={harness.RequestCount("/feed.xml")}, ErrorCount={(harness.Store.GetRow(feedUrl)?.ErrorCount ?? -1)}");

            await Task.Delay(Settle, CancellationToken.None);   // 400 ms = 2× the 200 ms interval — let ≥1 later cycle run so the exact count proves backoff suppression, not "nothing has happened yet"
        }
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }

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

        var feedUrl = harness.FeedUrl("/feed.xml");

        try
        {
            // Auto-removal implies the first fetch + failure already happened — poll for it instead of guessing a fixed delay.
            // 3 s deadline ≈ ~230 % headroom over the observed ~905 ms worst first-request latency on a loaded CI runner.
            await WaitUntilAsync(
                () => harness.Tracker.IsAutoRemoved(feedUrl),
                TimeSpan.FromSeconds(3),
                () => $"auto-removal after the first failure — RequestCount={harness.RequestCount("/feed.xml")}, IsAutoRemoved={harness.Tracker.IsAutoRemoved(feedUrl)}");

            await Task.Delay(Settle, CancellationToken.None);   // 400 ms = 2× the 200 ms interval — let ≥1 later cycle run so the exact count proves skip-without-fetch
        }
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }

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

        var feedUrl = harness.FeedUrl("/private/feed.xml");

        try
        {
            // Poll until the first cycle has consulted robots.txt (each cycle returns RobotsBlocked — site policy, not feed health).
            // The fixed 500 ms delay this replaces expired before the consult landed in CI run 40779636726 (Actual 0).
            // 3 s deadline ≈ ~230 % headroom over the observed ~905 ms worst first-request latency on a loaded CI runner.
            await WaitUntilAsync(
                () => harness.RequestCount("/robots.txt") >= 1,
                TimeSpan.FromSeconds(3),
                () => $"first cycle consulted robots.txt — RequestCount={harness.RequestCount("/robots.txt")}");

            await Task.Delay(Settle, CancellationToken.None);   // 400 ms = 2× the 200 ms interval — let ≥1 later cycle run so "consulted once" proves per-origin caching
        }
        finally { cts.Cancel(); await scheduler.StopAsync(CancellationToken.None); }

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
