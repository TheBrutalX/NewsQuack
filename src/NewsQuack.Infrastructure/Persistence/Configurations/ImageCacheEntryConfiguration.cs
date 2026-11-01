using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NewsQuack.Infrastructure.Persistence.Configurations;

public sealed class ImageCacheEntryConfiguration : IEntityTypeConfiguration<ImageCacheEntry>
{
    public void Configure(EntityTypeBuilder<ImageCacheEntry> builder)
    {
        builder.ToTable("image_cache_entries");

        builder.HasKey(e => e.Url);
        builder.Property(e => e.Url).HasColumnName("url");                       // primary key: the original image URL being cached

        builder.Property(e => e.ResolvedUrl).HasColumnName("resolved_url");
        builder.Property(e => e.CachedAtUtc).HasColumnName("cached_at_utc");
    }
}
