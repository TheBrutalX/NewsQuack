using NewsQuack.Core;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-014: in-memory recording <see cref="IStateStore"/> fake for the dedup tests (no SQLite). Records every call with its exact arguments so
/// delegation can be asserted. The delivered set is keyed by (instanceId, itemId) — the per-instance dimension of the contract. Single-threaded
/// tests — no locking needed.
/// </summary>
internal sealed class RecordingStateStore : IStateStore
{
    private readonly Dictionary<(string InstanceId, string ItemId), byte> _delivered = new();

    /// <summary>Every IsDeliveredAsync call in order (arguments as received).</summary>
    public List<(string InstanceId, string ItemId)> IsDeliveredCalls { get; } = [];

    /// <summary>Every MarkDeliveredAsync call in order — all arguments as received, including the FeedState reference itself (pass-through check).</summary>
    public List<(string InstanceId, string FeedUrl, string ItemId, DateTimeOffset DeliveredAtUtc, FeedState? NewFeedState)> MarkDeliveredCalls { get; } = [];

    /// <summary>When set, IsDeliveredAsync throws this instead of answering (pins that the stage propagates store failures — it must not swallow them).</summary>
    public Exception? IsDeliveredException { get; set; }

    /// <summary>When set, MarkDeliveredAsync throws this AFTER recording the call but BEFORE marking delivered (T-021: simulates a store failure at confirm time).</summary>
    public Exception? MarkDeliveredException { get; set; }

    /// <summary>True when the item is marked delivered for that instance — the worker's observable "confirmed" state.</summary>
    public bool IsDelivered(string instanceId, string itemId) => _delivered.ContainsKey((instanceId, itemId));

    /// <summary>Seeds the delivered state directly (simulates a confirm from an earlier cycle without going through MarkDeliveredAsync).</summary>
    public void SeedDelivered(string instanceId, string itemId) => _delivered[(instanceId, itemId)] = 0;

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

        _delivered[(instanceId, itemId)] = 0;   // the real store marks delivered atomically with the state advance — same observable effect here
        return Task.CompletedTask;
    }

    public Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default) => Task.FromResult(0);
}
