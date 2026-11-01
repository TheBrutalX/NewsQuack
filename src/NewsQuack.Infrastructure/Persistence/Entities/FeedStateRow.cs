namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Persistent per-feed state (last seen item, ETag, error tracking). Date properties are UTC DateTime — the EF Core SQLite provider cannot translate DateTimeOffset in WHERE/ORDER BY.</summary>
public class FeedStateRow
{
    public string FeedUrl { get; set; } = null!;          // primary key (configured fluently)
    public DateTime? LastItemPublishedAtUtc { get; set; } // UTC instant (store converts at the boundary)
    public string? Etag { get; set; }
    public DateTime? LastFetchedAtUtc { get; set; }       // UTC instant (store converts at the boundary)
    public int ErrorCount { get; set; }                   // default 0 (configured fluently)
    public DateTime? LastErrorAtUtc { get; set; }         // UTC instant (store converts at the boundary)
}
