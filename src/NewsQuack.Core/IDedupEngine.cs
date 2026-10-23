namespace NewsQuack.Core;

/// <summary>Persistent dedup per item-ID; at-least-once semantics (a crash between send and confirm may re-deliver once, never lose).</summary>
public interface IDedupEngine
{
    Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default);
    Task ConfirmDeliveryAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default);  // called by the send worker ONLY after the target answered "delivered"; advances state atomically
}
