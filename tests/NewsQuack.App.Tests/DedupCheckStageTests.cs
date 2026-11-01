using NewsQuack.App.Pipeline;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-014 unit: DedupCheckStage against a recording in-memory IStateStore fake (no SQLite) + LogCapture for log assertions. Pins the skip behavior,
/// the per-instance dimension (decision 2), the frozen metadata (Order 10 / "dedup-check" / IsolateItem), and that store failures propagate —
/// applying the policy is the runner's job, not the stage's.
/// </summary>
public class DedupCheckStageTests
{
    private const string ItemId = "item-1";

    private static (DedupCheckStage Stage, RecordingStateStore Store, LogCapture Logs) CreateStage()
    {
        var store = new RecordingStateStore();
        var logs = new LogCapture();
        var stage = new DedupCheckStage(new DedupEngine(store, logs.For<DedupEngine>()), logs.For<DedupCheckStage>());

        return (stage, store, logs);
    }

    [Fact]
    public async Task DedupCheck_AlreadyDeliveredItem_SetsSkipTrueAndLeavesPostUntouched()
    {
        var (stage, store, logs) = CreateStage();
        store.SeedDelivered(DedupTestPost.InstanceId, ItemId);

        var ctx = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        var original = ctx.CurrentPost;   // captured before the stage runs — asserted untouched after

        await stage.ExecuteAsync(ctx);

        Assert.True(ctx.Skip);
        Assert.Same(original, ctx.CurrentPost);          // never replaced...
        Assert.Equal(original, ctx.CurrentPost);         // ...and never mutated (the stage only reads + sets Skip)
        // The lookup used THIS instance's id + the post's item id — exactly one.
        Assert.Single(store.IsDeliveredCalls);
        Assert.Equal((DedupTestPost.InstanceId, ItemId), store.IsDeliveredCalls[0]);
        // Debug log with instanceId + itemId (T-014 decision 1).
        Assert.NotNull(logs.Find(LogLevel.Debug, DedupTestPost.InstanceId, ItemId));
    }

    [Fact]
    public async Task DedupCheck_NewItem_LeavesSkipFalse()
    {
        var (stage, store, logs) = CreateStage();   // nothing seeded — a brand-new item

        var ctx = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);

        await stage.ExecuteAsync(ctx);

        Assert.False(ctx.Skip);   // untouched — the item proceeds to the next stages
        Assert.Single(store.IsDeliveredCalls);   // the check still happened (cheap indexed lookup on the common path)
        Assert.Equal((DedupTestPost.InstanceId, ItemId), store.IsDeliveredCalls[0]);
        Assert.Null(logs.Find(LogLevel.Debug, DedupTestPost.InstanceId));   // nothing to log — no skip
    }

    [Fact]
    public async Task DedupCheckStage_Execute_ItemPendingButUnconfirmed_Skips()
    {
        var (stage, store, logs) = CreateStage();
        // T-049: the item is IN FLIGHT — enqueued but never confirmed (delivered set empty, pending set has it). A slow drain must not re-enqueue it.
        store.SeedPending(DedupTestPost.InstanceId, ItemId);

        var ctx = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);
        var original = ctx.CurrentPost;   // captured before the stage runs — asserted untouched after

        await stage.ExecuteAsync(ctx);

        Assert.True(ctx.Skip);   // skipped while pending — no second enqueue (and thus no second send) on this poll
        Assert.Same(original, ctx.CurrentPost);          // never replaced...
        Assert.Equal(original, ctx.CurrentPost);         // ...and never mutated (the stage only reads + sets Skip)
        // The delivered check ran first and found nothing; the pending check then caught the in-flight item — exactly one call each.
        Assert.Single(store.IsDeliveredCalls);
        Assert.Single(store.IsPendingCalls);
        Assert.Equal((DedupTestPost.InstanceId, ItemId), store.IsPendingCalls[0]);   // keyed by THIS instance's id + the post's item id
        // Debug log with instanceId + itemId — the observable skip reason (same shape as the delivered-skip log).
        Assert.NotNull(logs.Find(LogLevel.Debug, DedupTestPost.InstanceId, ItemId));
    }

    [Fact]
    public async Task DedupCheck_DeliveredInOtherInstance_NotSkippedForThisInstance()
    {
        var (stage, store, _) = CreateStage();
        // Decision 2 (load-bearing): the SAME itemId was already delivered to a DIFFERENT instance.
        store.SeedDelivered(DedupTestPost.OtherInstanceId, ItemId);

        var ctx = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);

        await stage.ExecuteAsync(ctx);

        Assert.False(ctx.Skip);   // multi-delivery of one item is allowed by the contract
        // The check was made with THIS instance's id — never the other one (a wrong key would have skipped here).
        Assert.Single(store.IsDeliveredCalls);
        Assert.Equal((DedupTestPost.InstanceId, ItemId), store.IsDeliveredCalls[0]);
    }

    [Fact]
    public void DedupCheck_Metadata_OrderTenStableNameIsolateItemPolicy()
    {
        var (stage, _, _) = CreateStage();

        Assert.Equal(10, stage.Order);                          // first stage — before filters (20), images (30), send (100)
        Assert.Equal("dedup-check", stage.Name);                // stable string for structured logging
        Assert.Equal(FailurePolicy.IsolateItem, stage.FailurePolicy);   // the norm: on failure the runner drops the item
    }

    [Fact]
    public async Task DedupCheck_StoreThrows_PropagatesException()
    {
        var (stage, store, _) = CreateStage();
        store.IsDeliveredException = new InvalidOperationException("sqlite busy");   // simulated store failure mid-check

        var ctx = DedupTestPost.Context(DedupTestPost.InstanceId, ItemId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(ctx));   // the stage must NOT swallow it
        Assert.False(ctx.Skip);   // ...and must not half-process: policy application (drop the item) is the runner's job
    }
}
