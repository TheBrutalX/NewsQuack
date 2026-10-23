namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>One row per delivered (instanceId, itemId) pair — the persistent dedup key.</summary>
public class DeliveredItem
{
    public long Id { get; set; }
    public string InstanceId { get; set; } = null!;
    public string ItemId { get; set; } = null!;
    public string FeedUrl { get; set; } = null!;
    public string? Title { get; set; }

    /// <summary>UTC instant. DateTime (not DateTimeOffset): the EF Core SQLite provider cannot translate DateTimeOffset in WHERE/ORDER BY — the store converts at the boundary.</summary>
    public DateTime DeliveredAtUtc { get; set; }
}
