namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Persistent per-feed state (last seen item, ETag, error tracking).</summary>
public class FeedStateRow
{
    public string FeedUrl { get; set; } = null!;          // primary key (configured fluently)
    public DateTimeOffset? LastItemPublishedAtUtc { get; set; }
    public string? Etag { get; set; }
    public DateTimeOffset? LastFetchedAtUtc { get; set; }
    public int ErrorCount { get; set; }                   // default 0 (configured fluently)
    public DateTimeOffset? LastErrorAtUtc { get; set; }
}
