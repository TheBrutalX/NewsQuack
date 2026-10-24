namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>T-049: one row per enqueued-but-unconfirmed (instanceId, itemId) pair — the in-flight marker. The dedup stage skips items that are delivered OR pending, so a slow drain (e.g. a rate-limited backlog) no longer re-enqueues — and re-sends — an in-flight item on every poll. The row is cleared inside the atomic confirm transaction and on dead-letter; whatever survives belongs to a dead process and is wiped at startup.</summary>
public class PendingItem
{
    public long Id { get; set; }
    public string InstanceId { get; set; } = null!;
    public string ItemId { get; set; } = null!;

    /// <summary>UTC instant of the (re-)mark. DateTime (not DateTimeOffset): the EF Core SQLite provider cannot translate DateTimeOffset in WHERE/ORDER BY — same convention as DeliveredItem.DeliveredAtUtc.</summary>
    public DateTime EnqueuedAtUtc { get; set; }
}
