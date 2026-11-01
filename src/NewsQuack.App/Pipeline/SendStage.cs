using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-024: the terminal pipeline stage (coordinator decision 4). Enqueues <see cref="PostContext.CurrentPost"/> into the send queue of
/// <see cref="PostContext.InstanceId"/> via the <see cref="SendQueueRegistry"/> — this is where a post leaves the pipeline and enters delivery.
/// T-049: before enqueuing it marks the item PENDING in the state store, so a slow drain (drain slower than the poll interval) no longer re-enqueues —
/// and re-sends — an in-flight item on every poll; the marker is cleared by the confirm transaction or dead-lettering, and wiped at startup.
/// Order 100, the last stage; its failure policy is <see cref="FailurePolicy.IsolateItem"/> (the norm).
/// </summary>
public sealed class SendStage : IPostStage
{
    private readonly SendQueueRegistry _queues;   // one bounded queue per configured instance — never a concrete queue type here
    private readonly IStateStore _stateStore;     // T-049: the in-flight marker — written BEFORE enqueue so stage-level coverage includes a crash before dequeue
    private readonly ILogger<SendStage> _logger;

    public SendStage(SendQueueRegistry queues, IStateStore stateStore, ILogger<SendStage> logger)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(logger);

        _queues = queues;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <summary>Order 100 — the last stage, after dedup (10), filters (20), image resolution (30) and the reserved LLM slot (40).</summary>
    public int Order => 100;

    /// <summary>Stable name for structured logging.</summary>
    public string Name => "send";

    /// <summary>The norm: on enqueue failure the runner drops the item — see the at-least-once note on <see cref="ExecuteAsync"/>.</summary>
    public FailurePolicy FailurePolicy => FailurePolicy.IsolateItem;

    /// <summary>
    /// Marks this item pending (T-049), then enqueues its post into its instance's queue. AT-LEAST-ONCE (decision 4): a marker or enqueue failure means the
    /// item was never confirmed as delivered ⇒ the next cycle re-delivers it — a post is never LOST, only ever (rarely) duplicated. The registry throws
    /// <see cref="KeyNotFoundException"/> for an unknown instance id; that propagates out of this stage and the runner isolates the item.
    /// </summary>
    public async Task ExecuteAsync(PostContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var queue = _queues.For(ctx.InstanceId);   // throws KeyNotFoundException naming the id for an unconfigured instance — surfaced by the runner (before any marker write)

        // T-049: mark in-flight BEFORE enqueueing — stage-level coverage includes a crash before dequeue. A marker failure propagates like any stage
        // failure (IsolateItem): the item was never confirmed, so the next cycle re-delivers it (at-least-once).
        await _stateStore.MarkPendingAsync(ctx.InstanceId, ctx.CurrentPost.ItemId, ct);

        await queue.EnqueueAsync(ctx.CurrentPost, ct);   // FullMode.Wait: a full queue delays here (never drops) until the worker frees space

        _logger.LogDebug("Post enqueued for delivery: instance {InstanceId}, item {ItemId}", ctx.InstanceId, ctx.CurrentPost.ItemId);
    }
}
