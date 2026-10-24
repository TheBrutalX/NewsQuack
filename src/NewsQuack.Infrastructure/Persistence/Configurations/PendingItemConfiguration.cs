using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NewsQuack.Infrastructure.Persistence.Configurations;

public sealed class PendingItemConfiguration : IEntityTypeConfiguration<PendingItem>
{
    public void Configure(EntityTypeBuilder<PendingItem> builder)
    {
        builder.ToTable("pending_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();   // autoincrement

        builder.Property(e => e.InstanceId).HasColumnName("instance_id");
        builder.Property(e => e.ItemId).HasColumnName("item_id");
        builder.Property(e => e.EnqueuedAtUtc).HasColumnName("enqueued_at_utc");

        builder.HasIndex(e => new { e.InstanceId, e.ItemId }).IsUnique();       // T-049: one marker row per (instance, item) — the safety net for re-marks
    }
}
