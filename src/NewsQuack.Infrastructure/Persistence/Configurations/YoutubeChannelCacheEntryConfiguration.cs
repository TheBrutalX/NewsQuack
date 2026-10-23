using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NewsQuack.Infrastructure.Persistence.Configurations;

public sealed class YoutubeChannelCacheEntryConfiguration : IEntityTypeConfiguration<YoutubeChannelCacheEntry>
{
    public void Configure(EntityTypeBuilder<YoutubeChannelCacheEntry> builder)
    {
        builder.ToTable("youtube_channel_cache_entries");

        builder.HasKey(e => e.ChannelUrl);
        builder.Property(e => e.ChannelUrl).HasColumnName("channel_url");        // primary key: one row per channel URL

        builder.Property(e => e.LastCheckedAtUtc).HasColumnName("last_checked_at_utc");
        builder.Property(e => e.LastVideoPublishedAtUtc).HasColumnName("last_video_published_at_utc");
    }
}
