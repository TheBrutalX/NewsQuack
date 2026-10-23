using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NewsQuack.Infrastructure.Persistence.Configurations;

public sealed class DeadLetterEntryConfiguration : IEntityTypeConfiguration<DeadLetterEntry>
{
    public void Configure(EntityTypeBuilder<DeadLetterEntry> builder)
    {
        builder.ToTable("dead_letter_entries");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();   // autoincrement

        builder.Property(e => e.InstanceId).HasColumnName("instance_id");
        builder.Property(e => e.FeedUrl).HasColumnName("feed_url");
        builder.Property(e => e.ItemId).HasColumnName("item_id");
        builder.Property(e => e.PayloadJson).HasColumnName("payload_json");
        builder.Property(e => e.Attempts).HasColumnName("attempts");
        builder.Property(e => e.ErrorCategory).HasColumnName("error_category");  // "429" / "5xx" / "timeout" / "400-payload"
        builder.Property(e => e.LastError).HasColumnName("last_error");
        builder.Property(e => e.FailedAtUtc).HasColumnName("failed_at_utc");
    }
}
