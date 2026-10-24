using NewsQuack.Core;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-014: in-memory recording <see cref="IStateStore"/> fake for the dedup tests (no SQLite). Records every call with its exact arguments so
/// delegation can be asserted. The delivered AND pending (T-049) sets are keyed by (instanceId, itemId) — the per-instance dimension of the contract;
/// MarkDeliveredAsync and RecordDeadLetterAsync clear the pending set exactly like the real store's atomic commits. Single-threaded tests — no locking needed.
/// </summary>
internal sealed class RecordingStateStore : IStateStore
{
    private readonly Dictionary<(string InstanceId, string ItemId), byte> _delivered = new();
    private readonly Dictionary<(string InstanceId, string ItemId), byte> _pending = new();

    /// <summary>Every IsDeliveredAsync call in order (arguments as received).</summary>
    public List<(string InstanceId, string ItemId)> IsDeliveredCalls { get; } = [];

    /// <summary>Every MarkDeliveredAsync call in order — all arguments as received, including the FeedState reference itself (pass-through check).</summary>
    public List<(string InstanceId, string FeedUrl, string ItemId, DateTimeOffset DeliveredAtUtc, FeedState? NewFeedState)> MarkDeliveredCalls { get; } = [];

    /// <summary>When set, IsDeliveredAsync throws this instead of answering (pins that the stage propagates store failures — it must not swallow them).</summary>
    public Exception? IsDeliveredException { get; set; }

    /// <summary>When set, MarkDeliveredAsync throws this AFTER recording the call but BEFORE marking delivered (T-021: simulates a store failure at confirm time).</summary>
    public Exception? MarkDeliveredException { get; set; }

    /// <summary>Every RecordDeadLetterAsync call in order — all arguments as received (T-022: dead-letter persistence is assertable).</summary>
    public List<DeadLetterData> RecordDeadLetterCalls { get; } = [];

    /// <summary>When set, RecordDeadLetterAsync throws this AFTER recording the call but BEFORE persisting (T-022: simulates a store failure at dead-letter time).</summary>
    public Exception? RecordDeadLetterException { get; set; }

    /// <summary>Every MarkPendingAsync call in order (T-049: the in-flight marker write is assertable).</summary>
    public List<(string InstanceId, string ItemId)> MarkPendingCalls { get; } = [];

    /// <summary>Every ClearPendingAsync call in order (T-049).</summary>
    public List<(string InstanceId, string ItemId)> ClearPendingCalls { get; } = [];

    /// <summary>Every IsPendingAsync call in order (T-049).</summary>
    public List<(string InstanceId, string ItemId)> IsPendingCalls { get; } = [];

    /// <summary>True when the item is marked delivered for that instance — the worker's observable "confirmed" state.</summary>
    public bool IsDelivered(string instanceId, string itemId) => _delivered.ContainsKey((instanceId, itemId));

    /// <summary>Seeds the delivered state directly (simulates a confirm from an earlier cycle without going through MarkDeliveredAsync).</summary>
    public void SeedDelivered(string instanceId, string itemId) => _delivered[(instanceId, itemId)] = 0;

    /// <summary>True when the item is marked pending for that instance — the worker's observable "in flight" state (T-049).</summary>
    public bool IsPending(string instanceId, string itemId) => _pending.ContainsKey((instanceId, itemId));

    /// <summary>Seeds the pending state directly (simulates a marker write from an earlier cycle without going through MarkPendingAsync — T-049).</summary>
    public void SeedPending(string instanceId, string itemId) => _pending[(instanceId, itemId)] = 0;

    public Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default) => Task.FromResult<FeedState?>(null);

    public Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default) => Task.CompletedTask;   // dedup never touches feed-state rows directly

    public Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        IsDeliveredCalls.Add((instanceId, itemId));

        if (IsDeliveredException is { } ex)
            throw ex;   // simulate a store failure mid-check

        return Task.FromResult(_delivered.ContainsKey((instanceId, itemId)));
    }

    public Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default)
    {
        MarkDeliveredCalls.Add((instanceId, feedUrl, itemId, deliveredAtUtc, newFeedState));

        if (MarkDeliveredException is { } ex)
            throw ex;   // the call was recorded but the item stays UNDELIVERED — exactly what a store failure at confirm time means (T-021)

        _delivered[(instanceId, itemId)] = 0;    // the real store marks delivered atomically with the state advance — same observable effect here...
        _pending.Remove((instanceId, itemId));   // ...and clears the pending marker in the SAME commit (T-049) — a failed confirm leaves it pending
        return Task.CompletedTask;
    }

    public Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default) => Task.FromResult(0);

    public Task RecordDeadLetterAsync(DeadLetterData deadLetter, CancellationToken ct = default)
    {
        RecordDeadLetterCalls.Add(deadLetter);

        if (RecordDeadLetterException is { } ex)
            throw ex;   // the call was recorded but the row stays UNPERSISTED — exactly what a store failure at dead-letter time means (T-022)

        _pending.Remove((deadLetter.InstanceId, deadLetter.ItemId));   // T-049: the real store clears the pending marker in the same SaveChanges as the dead-letter upsert
        return Task.CompletedTask;
    }

    public Task MarkPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        MarkPendingCalls.Add((instanceId, itemId));   // T-049: recorded BEFORE the state change — a re-mark is the normal case and simply succeeds (upsert)

        _pending[(instanceId, itemId)] = 0;
        return Task.CompletedTask;
    }

    public Task ClearPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        ClearPendingCalls.Add((instanceId, itemId));

        _pending.Remove((instanceId, itemId));   // no-op when absent — same as the real store
        return Task.CompletedTask;
    }

    public Task<bool> IsPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        IsPendingCalls.Add((instanceId, itemId));

        return Task.FromResult(_pending.ContainsKey((instanceId, itemId)));
    }

    public Task<int> CleanupAllPendingAsync(CancellationToken ct = default)
    {
        var count = _pending.Count;   // T-049: startup cleanup — every pending entry belonged to a dead process
        _pending.Clear();

        return Task.FromResult(count);
    }
}
