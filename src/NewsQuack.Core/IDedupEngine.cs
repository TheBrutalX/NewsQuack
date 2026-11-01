namespace NewsQuack.Core;

/// <summary>Persistent dedup per item-ID; at-least-once semantics (a crash between send and confirm may re-deliver once, never lose).</summary>
public interface IDedupEngine
{
    Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default);
    Task<bool> IsPendingAsync(string instanceId, string itemId, CancellationToken ct = default);   // T-049: in-flight check — mirrors IsDeliveredAsync (thin delegation to IStateStore); the dedup stage skips delivered OR pending
    Task ConfirmDeliveryAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default);  // called by the send worker ONLY after the target answered "delivered"; advances state atomically (+ T-049: clears the pending marker in the same commit)
}
