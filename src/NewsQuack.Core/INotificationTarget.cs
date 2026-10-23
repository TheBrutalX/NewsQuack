namespace NewsQuack.Core;

public enum TargetOutcome { Delivered, Retryable, PermanentFailure }

public sealed record TargetResult(
    TargetOutcome Outcome,
    TimeSpan? RetryAfter = null,   // meaningful only when Outcome == Retryable; the worker waits at least this long
    string? Error = null)          // short human-readable error for logging / dead-letter
{
    public static TargetResult Delivered() => new(TargetOutcome.Delivered);
    public static TargetResult Retryable(TimeSpan? retryAfter, string? error = null) => new(TargetOutcome.Retryable, retryAfter, error);
    public static TargetResult PermanentFailure(string? error = null) => new(TargetOutcome.PermanentFailure, null, error);
}

/// <summary>Target-agnostic delivery contract; the send worker owns all retry logic, never the target.</summary>
public interface INotificationTarget
{
    Task<TargetResult> SendAsync(Post post, CancellationToken ct = default);   // target-specific config (e.g. webhook URL) is injected at construction — one instance per configured instance
}
