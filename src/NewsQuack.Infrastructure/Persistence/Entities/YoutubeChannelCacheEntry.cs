namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Cached check results per YouTube channel.</summary>
public class YoutubeChannelCacheEntry
{
    public string ChannelUrl { get; set; } = null!;       // primary key (configured fluently)
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public DateTimeOffset? LastVideoPublishedAtUtc { get; set; }
}
