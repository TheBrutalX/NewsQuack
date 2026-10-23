namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Cached check results per YouTube channel. Date properties are UTC DateTime — the EF Core SQLite provider cannot translate DateTimeOffset in WHERE/ORDER BY.</summary>
public class YoutubeChannelCacheEntry
{
    public string ChannelUrl { get; set; } = null!;       // primary key (configured fluently)
    public DateTime? LastCheckedAtUtc { get; set; }       // UTC instant (store converts at the boundary)
    public DateTime? LastVideoPublishedAtUtc { get; set; } // UTC instant (store converts at the boundary)
}
