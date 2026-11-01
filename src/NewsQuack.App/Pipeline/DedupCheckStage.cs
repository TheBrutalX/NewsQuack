using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-014: pipeline stage that skips items already delivered to THIS instance — Order 10, the first stage, so no enrichment work happens on an
/// item that will not be sent. T-049: it ALSO skips items that are PENDING (enqueued but not yet confirmed) — a slow drain (drain slower than the
/// poll interval) must not re-enqueue, and re-send, in-flight items on every poll. The checks always pass <see cref="PostContext.InstanceId"/>
/// (delivery is per-instance — the same item delivered to instance A must NOT be skipped for instance B). The stage only READS
/// <see cref="PostContext.CurrentPost"/> and sets <see cref="PostContext.Skip"/>; it never mutates or replaces the post. Any exception propagates
/// unchanged — applying the <see cref="FailurePolicy.IsolateItem"/> policy (drop the item) is the runner's job (a later task), not this stage's.
/// Stateless singleton: one instance serves all instances/feeds, thread-safety comes from the engine/store.
/// </summary>
public sealed class DedupCheckStage : IPostStage
{
    private readonly IDedupEngine _dedup;
    private readonly ILogger<DedupCheckStage> _logger;

    public DedupCheckStage(IDedupEngine dedup, ILogger<DedupCheckStage> logger)
    {
        ArgumentNullException.ThrowIfNull(dedup);
        ArgumentNullException.ThrowIfNull(logger);

        _dedup = dedup;
        _logger = logger;
    }

    /// <summary>Order 10 — runs before filters (20), image resolution (30) and the send stage (100).</summary>
    public int Order => 10;

    /// <summary>Stable name for structured logging.</summary>
    public string Name => "dedup-check";

    /// <summary>The norm: on stage failure the runner drops the item (a store failure during the check loses this one item, never the feed).</summary>
    public FailurePolicy FailurePolicy => FailurePolicy.IsolateItem;

    public async Task ExecuteAsync(PostContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // One indexed SQLite lookup on the common path (T-014: cheap — no extra queries, no scanning). Per-instance by construction. Delivered is checked first...
        if (await _dedup.IsDeliveredAsync(ctx.InstanceId, ctx.CurrentPost.ItemId, ct))
        {
            ctx.Skip = true;   // stop processing of this item — no further work on it

            _logger.LogDebug("Item already delivered to this instance — skipping: instance {InstanceId}, item {ItemId}",
                ctx.InstanceId, ctx.CurrentPost.ItemId);

            return;   // a delivered item's pending marker was cleared in the same commit — nothing more to check
        }

        // ...T-049: an enqueued-but-unconfirmed item is IN FLIGHT. Skipping it stops a slow drain from re-enqueueing (and re-sending) it on every poll.
        if (await _dedup.IsPendingAsync(ctx.InstanceId, ctx.CurrentPost.ItemId, ct))
        {
            ctx.Skip = true;

            _logger.LogDebug("Item already in flight for this instance — skipping: instance {InstanceId}, item {ItemId}",
                ctx.InstanceId, ctx.CurrentPost.ItemId);
        }
    }
}
