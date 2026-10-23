using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Pipeline;

/// <summary>
/// T-014: persistent dedup per item-ID — a thin App-level engine over the frozen <see cref="IStateStore"/> contract. No logic of its own:
/// <see cref="IsDeliveredAsync"/> is one indexed lookup, <see cref="ConfirmDeliveryAsync"/> is ONE atomic transaction (mark item delivered +
/// advance feed state) inside the store. Stateless singleton serving all instances/feeds — thread-safety comes from the store (one context per
/// operation). No caching in v1.0: SQLite reads are cheap; a cache is a later-SP optimization (T-014 decision 1).
/// <para>
/// Delivery is PER-INSTANCE: every call must pass the instance it belongs to — an item delivered to instance A is NOT delivered for instance B
/// (the contract allows multi-delivery of one item). The stage pins this by always passing <c>ctx.InstanceId</c>.
/// </para>
/// <para>
/// At-least-once semantics (T-014 decision 3 — documented here and pinned by the simulated-crash tests): delivery is confirmed ONLY via
/// <see cref="ConfirmDeliveryAsync"/> after the target answered "delivered" — the send worker (a later task) is the ONLY caller. A crash between
/// send and confirm leaves the item unconfirmed, so the next poll cycle re-parses it from the feed, fails the dedup check, and enqueues/sends it
/// again: one duplicate possible, never a loss (bounded by how long the feed keeps serving the item). "Re-enqueue at start" is therefore NOT a
/// separate mechanism in v1.0 — it falls out of re-polling + persistent dedup. Known limitation (documented, accepted): an item that both fell
/// off the feed AND was unconfirmed at crash time is lost — same property as the reference project.
/// </para>
/// </summary>
public sealed class DedupEngine : IDedupEngine
{
    private readonly IStateStore _store;
    private readonly ILogger<DedupEngine> _logger;

    public DedupEngine(IStateStore store, ILogger<DedupEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>Delegates unchanged to the store — no caching layer in front (T-014 decision 1).</remarks>
    public Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default) =>
        _store.IsDeliveredAsync(instanceId, itemId, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Called by the send worker ONLY after the target answered "delivered" (ownership pinned by the frozen contract's doc comment). All
    /// arguments pass through to the store unchanged — including the <see cref="FeedState"/> record reference, which is never copied or rebuilt.
    /// The Info log below is the observable "delivered" moment (T-014 decision 1). A store failure propagates to the worker, which owns
    /// retry/dead-letter (a later task).
    /// </remarks>
    public async Task ConfirmDeliveryAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default)
    {
        await _store.MarkDeliveredAsync(instanceId, feedUrl, itemId, deliveredAtUtc, newFeedState, ct);

        _logger.LogInformation("Item delivered: instance {InstanceId}, feed {FeedUrl}, item {ItemId}", instanceId, feedUrl, itemId);
    }
}
