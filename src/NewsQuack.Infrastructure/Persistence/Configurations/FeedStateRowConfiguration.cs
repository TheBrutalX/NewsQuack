using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NewsQuack.Infrastructure.Persistence.Configurations;

public sealed class FeedStateRowConfiguration : IEntityTypeConfiguration<FeedStateRow>
{
    public void Configure(EntityTypeBuilder<FeedStateRow> builder)
    {
        builder.ToTable("feed_state_rows");

        builder.HasKey(e => e.FeedUrl);
        builder.Property(e => e.FeedUrl).HasColumnName("feed_url");              // primary key: one row per feed URL

        builder.Property(e => e.LastItemPublishedAtUtc).HasColumnName("last_item_published_at_utc");
        builder.Property(e => e.Etag).HasColumnName("etag");
        builder.Property(e => e.LastFetchedAtUtc).HasColumnName("last_fetched_at_utc");
        builder.Property(e => e.ErrorCount).HasColumnName("error_count").HasDefaultValue(0);
        builder.Property(e => e.LastErrorAtUtc).HasColumnName("last_error_at_utc");
    }
}
