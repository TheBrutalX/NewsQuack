namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Cached image resolution results, keyed by the original image URL.</summary>
public class ImageCacheEntry
{
    public string Url { get; set; } = null!;              // primary key: the original image URL being cached (configured fluently)
    public string? ResolvedUrl { get; set; }
    public DateTimeOffset CachedAtUtc { get; set; }
}
