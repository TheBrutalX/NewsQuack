namespace NewsQuack.Infrastructure.Persistence.Entities;

/// <summary>Persistent dead-letter for posts that exhausted their retry attempts. Date properties are UTC DateTime — the EF Core SQLite provider cannot translate DateTimeOffset in WHERE/ORDER BY.</summary>
public class DeadLetterEntry
{
    public long Id { get; set; }
    public string InstanceId { get; set; } = null!;
    public string FeedUrl { get; set; } = null!;
    public string ItemId { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;      // full post payload as JSON
    public int Attempts { get; set; }
    public string ErrorCategory { get; set; } = null!;    // "429" / "5xx" / "timeout" / "400-payload"
    public string? LastError { get; set; }

    /// <summary>UTC instant (store converts at the boundary).</summary>
    public DateTime FailedAtUtc { get; set; }
}
