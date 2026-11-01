using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NewsQuack.Infrastructure.Persistence.Configurations;

public sealed class DeliveredItemConfiguration : IEntityTypeConfiguration<DeliveredItem>
{
    public void Configure(EntityTypeBuilder<DeliveredItem> builder)
    {
        builder.ToTable("delivered_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();   // autoincrement

        builder.Property(e => e.InstanceId).HasColumnName("instance_id");
        builder.Property(e => e.ItemId).HasColumnName("item_id");
        builder.Property(e => e.FeedUrl).HasColumnName("feed_url");
        builder.Property(e => e.Title).HasColumnName("title");
        builder.Property(e => e.DeliveredAtUtc).HasColumnName("delivered_at_utc");

        builder.HasIndex(e => new { e.InstanceId, e.ItemId }).IsUnique();       // dedup key: one row per (instance, item)
    }
}
