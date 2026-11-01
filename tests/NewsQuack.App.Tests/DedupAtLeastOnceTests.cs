using NewsQuack.App.Pipeline;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-014: at-least-once round trips (simulated) against a recording in-memory IStateStore fake — the tests that pin decision 3. Delivery is
/// confirmed only via ConfirmDeliveryAsync after the target's delivered answer; a crash between send and confirm leaves the item unconfirmed, so
/// the next poll cycle re-parses it from the feed, fails the dedup check, and sends it again: one duplicate possible, never a loss. "Re-enqueue
/// at start" is not a separate mechanism — it falls out of re-polling + persistent dedup (each cycle below builds a fresh context, exactly what
/// re-parsing produces). The engine + stage are the real types; only the store is faked.
/// </summary>
public class DedupAtLeastOnceTests
{
    private const string ItemId = "item-1";

    /// <summary>The fixed injected instant — never a real clock (AGENTS.md UTC rule).</summary>
    private static readonly DateTimeOffset DeliveredAtUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static (DedupEngine Engine, DedupCheckStage Stage, RecordingStateStore Store) CreatePipeline()
    {
        var store = new RecordingStateStore();
        var logs = new LogCapture();

        return (new DedupEngine(store, logs.For<DedupEngine>()), new DedupCheckStage(new DedupEngine(store, logs.For<DedupEngine>()), logs.For<DedupCheckStage>()), store);
    }

    [Fact]
    public async Task ConfirmThenRecheck_ItemSkippedOnNextCycle_NoResend()
    {
        var (engine, stage, store) = CreatePipeline();

        // Cycle 1: the item is parsed and hits the stage — not skipped, so it is enqueued and sent. The target answers "delivered" and the
        // (simulated) send worker confirms — the ONLY moment delivery becomes persistent.
        var firstCycle = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        await stage.ExecuteAsync(firstCycle);
        Assert.False(firstCycle.Skip);

        await engine.ConfirmDeliveryAsync(DedupTestPost.InstanceId, DedupTestPost.FeedUrl, ItemId, DeliveredAtUtc, null);
        Assert.Single(store.MarkDeliveredCalls);   // exactly one confirm — the atomic mark + state advance happened

        // Cycle 2 (next poll): the feed still serves the item, so it is re-parsed and hits the stage again.
        var nextCycle = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        await stage.ExecuteAsync(nextCycle);

        Assert.True(nextCycle.Skip);   // dedup caught it — no second send (the "never lose" side of at-least-once: confirmed items stay confirmed)
    }

    [Fact]
    public async Task CrashBetweenSendAndConfirm_NextCycleRedelivers()
    {
        var (_, stage, store) = CreatePipeline();   // engine deliberately unused after the crash — nothing could confirm

        // Cycle 1: the item is parsed and hits the stage — not skipped, so it is enqueued and "sent" by the worker...
        var firstCycle = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        await stage.ExecuteAsync(firstCycle);
        Assert.False(firstCycle.Skip);

        // ...then the process crashes BEFORE ConfirmDeliveryAsync. No confirm call at all — the store never learned about the delivery.
        Assert.Empty(store.MarkDeliveredCalls);   // the crash: delivery was in flight, not yet persistent

        // Cycle 2 (after restart): the feed still serves the item, so it is re-parsed and hits the stage again. Unconfirmed ⇒ NOT skipped —
        // it is re-enqueued and sent again. This is the at-least-once guarantee pinned in one test: one duplicate possible, never a loss.
        var nextCycle = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        await stage.ExecuteAsync(nextCycle);

        Assert.False(nextCycle.Skip);   // re-delivery — the crash cost a duplicate, not the item itself
    }
}
