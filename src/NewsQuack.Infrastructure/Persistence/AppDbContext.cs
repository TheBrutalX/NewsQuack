using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace NewsQuack.Infrastructure.Persistence;

/// <summary>
/// Skeleton context: registers all six entities and applies the fluent configurations from this assembly.
/// No migrations yet (a later task adds migration 001 + factory registration); constructible with a plain
/// <see cref="DbContextOptions{AppDbContext}"/> pointing at any SQLite file so it can be tested.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeliveredItem> DeliveredItems => Set<DeliveredItem>();
    public DbSet<PendingItem> PendingItems => Set<PendingItem>();   // T-049: the in-flight marker (dedup skips delivered OR pending)
    public DbSet<FeedStateRow> FeedStateRows => Set<FeedStateRow>();
    public DbSet<YoutubeChannelCacheEntry> YoutubeChannelCacheEntries => Set<YoutubeChannelCacheEntry>();
    public DbSet<ImageCacheEntry> ImageCacheEntries => Set<ImageCacheEntry>();
    public DbSet<DeadLetterEntry> DeadLetterEntries => Set<DeadLetterEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
