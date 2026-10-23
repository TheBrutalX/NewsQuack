namespace NewsQuack.Core;

public sealed record FeedState(
    string FeedUrl,
    DateTimeOffset? LastItemPublishedAtUtc,
    string? Etag,
    DateTimeOffset? LastFetchedAtUtc,
    int ErrorCount = 0,
    DateTimeOffset? LastErrorAtUtc = null);   // explicit default: C# requires optional parameters to come after all required ones (CS1737)

/// <summary>Core-facing persistence contract; EF Core lives behind it in Infrastructure.</summary>
public interface IStateStore
{
    Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default);
    Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default);
    Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default);   // delivery is per-instance: the same item may be delivered by multiple instances
    Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default);  // ONE atomic transaction: mark item delivered + advance feed state
    Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default);   // retention; returns rows removed
}
