using NewsQuack.App.Pipeline;        // BoundedSendQueue — read back the posts SendStage enqueued (internal TryRead/PendingCount via InternalsVisibleTo)
using NewsQuack.Core;                // Post
using Microsoft.Extensions.Logging;  // LogLevel for log assertions
using WireMock.RequestBuilders;      // Request.Create() for stubbing feeds + robots.txt
using WireMock.ResponseBuilders;     // Response.Create() for stub responses
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-049 integration: the pending (in-flight) marker end to end over the REAL stage chain via SchedulerTestHarness — pins issue #57 (an enqueued-but-unconfirmed
/// item must not be re-enqueued on the next poll while a drain is slower than the poll interval) and the startup-cleanup half of at-least-once (a marker that
/// outlived its process belongs to a dead process and is wiped at boot, so the item is delivered again — redelivered after restart, never lost).
/// </summary>
public class InFlightDedupTests
{
    private const string InstanceId = "inst-a";

    /// <summary>A healthy one-item RSS — parses to exactly one post (same shape as InstanceSchedulerTests).</summary>
    private const string RssOneItem = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>t</title>" +
        "<item><title>Only post</title><link>https://example.com/only</link><guid isPermaLink=\"true\">https://example.com/only</guid>" +
        "<pubDate>Tue, 21 Oct 2025 16:42:10 -0700</pubDate></item></channel></rss>";

    /// <summary>The stable item id the parser derives from that RSS (the guid) — what SendStage marks pending and dedup checks.</summary>
    private const string ItemId = "https://example.com/only";

    /// <summary>Polls a condition every 5 ms until it holds or the (generous) timeout passes — scheduler work happens on its own task.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++)   // up to ~5 s — far beyond every pinned wait in these tests
            await Task.Delay(5);

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task Scheduler_SlowDrain_ItemEnqueuedNotConfirmed_NextPollDoesNotReEnqueue()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(300), ["/feed.xml"]);

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssOneItem));

        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);   // NO send worker — nothing dequeues or confirms (the "slow drain": the queue only ever fills)

        try
        {
            // Let at least three poll cycles complete: every one of them re-parses the same feed and sees the SAME unconfirmed item.
            await WaitUntilAsync(() => harness.RequestCount("/feed.xml") >= 3, "at least three poll cycles");
        }
        finally
        {
            cts.Cancel();
            await scheduler.StopAsync(CancellationToken.None);   // clean shutdown — a healthy run leaves no Error logs anywhere in the cycle
        }

        // Drain everything that accumulated — EXACTLY ONE entry total: the marker made every later poll skip the in-flight item (no re-enqueue, no second send).
        var queue = (BoundedSendQueue)harness.Registry.For(InstanceId);
        var collected = new List<Post>();
        while (queue.TryRead(out var post)) collected.Add(post);

        Assert.Single(collected);   // the pre-T-049 defect: one entry per poll (three here) — the item was sent twice whenever drain was slower than polling
        Assert.Equal(ItemId, collected[0].ItemId);   // ! safe: Single above guarantees the entry
        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }

    [Fact]
    public async Task Startup_CrashBetweenMarkerWriteAndEnqueue_StartupCleanupRemovesPending_ItemDeliveredOnNextCycle()
    {
        using var harness = SchedulerTestHarness.Create(InstanceId, TimeSpan.FromMilliseconds(300), ["/feed.xml"]);

        harness.Server.Given(Request.Create().WithPath("/feed.xml"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(RssOneItem));

        // Simulate the crash (T-049 crash point a): the send stage wrote the marker but the process died BEFORE enqueueing — the queue is empty, the row survives.
        await harness.Store.MarkPendingAsync(InstanceId, ItemId);
        Assert.True(harness.Store.IsPending(InstanceId, ItemId));   // the marker outlived its process...

        // ...and startup cleanup (what Program.MigrateStoreAsync runs at boot, before any worker starts) removes it — without this the item would be skipped forever.
        var removed = await harness.Store.CleanupAllPendingAsync();
        Assert.Equal(1, removed);   // exactly the one stale row...
        Assert.False(harness.Store.IsPending(InstanceId, ItemId));      // ...is gone (pending set empty)

        // Next start: a fresh scheduler over the SAME store (persistence is what survives a restart) — the first cycle re-parses and must NOT skip.
        var queue = (BoundedSendQueue)harness.Registry.For(InstanceId);   // still empty — the enqueue never happened in the "dead" process
        using var cts = new CancellationTokenSource();
        var scheduler = harness.BuildScheduler();
        await scheduler.StartAsync(cts.Token);

        try
        {
            await WaitUntilAsync(() => queue.PendingCount >= 1, "the item to be enqueued on the next cycle");   // PendingCount peeks without consuming — the entry stays for the assert below
        }
        finally
        {
            cts.Cancel();
            await scheduler.StopAsync(CancellationToken.None);
        }

        Post? enqueued = null;   // ! safe: WaitUntilAsync above guarantees at least one entry before we stop here
        while (queue.TryRead(out var post)) enqueued = post;

        Assert.NotNull(enqueued);   // delivered (enqueued) on the next cycle — the cleanup preserved at-least-once: redelivered after restart, never lost
        Assert.Equal(ItemId, enqueued!.ItemId);   // ! safe: NotNull above guarantees the entry
        Assert.Equal(0, harness.Logs.CountAt(LogLevel.Error));
    }
}
