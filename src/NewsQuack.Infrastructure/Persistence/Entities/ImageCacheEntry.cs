namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Cached image resolution results, keyed by the original image URL. Date properties are UTC DateTime — the EF Core SQLite provider cannot translate DateTimeOffset in WHERE/ORDER BY.</summary>
public class ImageCacheEntry
{
    public string Url { get; set; } = null!;              // primary key: the original image URL being cached (configured fluently)
    public string? ResolvedUrl { get; set; }

    /// <summary>UTC instant (store converts at the boundary).</summary>
    public DateTime CachedAtUtc { get; set; }
}
