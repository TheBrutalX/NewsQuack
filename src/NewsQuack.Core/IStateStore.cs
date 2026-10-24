namespace NewsQuack.Core;

public sealed record FeedState(
    string FeedUrl,
    DateTimeOffset? LastItemPublishedAtUtc,
    string? Etag,
    DateTimeOffset? LastFetchedAtUtc,
    int ErrorCount = 0,
    DateTimeOffset? LastErrorAtUtc = null);   // explicit default: C# requires optional parameters to come after all required ones (CS1737)

/// <summary>T-022: a persistent dead letter — one row per post that exhausted its retry budget or failed permanently. <see cref="PayloadJson"/> carries the FULL post as JSON (serialized by the caller — Core stays package-free); <see cref="ErrorCategory"/> is one of the four values pinned by the entity: "429" / "5xx" / "timeout" / "400-payload".</summary>
public sealed record DeadLetterData(
    string InstanceId,
    string FeedUrl,
    string ItemId,
    string PayloadJson,
    int Attempts,
    string ErrorCategory,
    string? LastError,
    DateTimeOffset FailedAtUtc);

/// <summary>Core-facing persistence contract; EF Core lives behind it in Infrastructure.</summary>
public interface IStateStore
{
    Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default);
    Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default);
    Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default);   // delivery is per-instance: the same item may be delivered by multiple instances
    Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default);  // ONE atomic transaction: mark item delivered + advance feed state
    Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default);   // retention; returns rows removed
    Task RecordDeadLetterAsync(DeadLetterData deadLetter, CancellationToken ct = default);   // T-022: persist a dead letter (full payload + classified category) — a failed record is covered by at-least-once: the item was never confirmed, so the next cycle re-delivers it
}
