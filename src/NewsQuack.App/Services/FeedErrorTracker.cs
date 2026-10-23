using System.Collections.Concurrent;   // ConcurrentDictionary for the process-lifetime auto-removed set (T-016 decision 6)
using NewsQuack.Core;
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Services;

/// <summary>The abstract outcome of one feed attempt (fetch + parse), consumed by <see cref="IFeedErrorTracker"/>. The mapping from the concrete
/// fetch/parse results to this record happens in the later scheduler task — T-016 decision 7 pins what counts as a failure.</summary>
public sealed record FeedAttemptOutcome(bool Success, string? FailureReason = null);

/// <summary>The tracker's decision for one recorded attempt. The caller (the later scheduler) applies the delay — the tracker only reports it;
/// there is no retry loop inside the tracker (T-016 non-goal).</summary>
public sealed record FeedErrorDecision(
    int ErrorCount,            // count AFTER this attempt (0 on success)
    TimeSpan NextAttemptDelay, // 0 on success; otherwise the backoff for decision 3
    bool AutoRemoved);         // true when this attempt crossed the threshold with auto-remove enabled

/// <summary>Per-feed error tracking contract (T-016): persisted error count + exponential backoff + sticky process-lifetime auto-removal. The later
/// scheduler/pipeline task resolves this — never the concrete type.</summary>
public interface IFeedErrorTracker
{
    /// <param name="nowUtc">The caller-injected UTC instant of this attempt (the later scheduler passes <c>DateTimeOffset.UtcNow</c>) — the tracker
    /// itself never reads a clock (AGENTS.md UTC rule).</param>
    Task<FeedErrorDecision> RecordAttemptAsync(string feedUrl, FeedAttemptOutcome outcome, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>True when the feed was auto-removed earlier in this process run. Sticky: successes do not clear it (T-016 decision 4).</summary>
    bool IsAutoRemoved(string feedUrl);

    /// <summary>The v1.0 "metric" for auto-removals (T-016 decision 5 — no metrics endpoint exists yet): incremented on every failure at or above the
    /// threshold while auto-remove is enabled.</summary>
    int AutoRemovedCount { get; }

    /// <summary>T-024 (additive, decision 7): remaining backoff for this feed at <paramref name="nowUtc"/> — zero when never failed or the last attempt succeeded.</summary>
    Task<TimeSpan> BackoffRemainingAsync(string feedUrl, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// T-016: per-feed error tracker. Every failure increments the persisted <see cref="FeedState.ErrorCount"/> (with <c>LastErrorAtUtc</c> set to the
/// injected instant) and reports an exponential backoff — 30 s × 2^(count−1), capped at exactly 6 h; with auto-remove enabled, a feed whose count
/// reaches the threshold is removed for the rest of this process run (Error log + counter). A success resets the persisted counter to zero but never
/// un-removes a feed (sticky v1.0 semantics; after a restart the persisted count re-triggers removal on the next failure — self-healing, no state
/// migration needed, T-016 decision 4/6). Removal lives in a process-lifetime set inside this singleton: no store schema change, no new migration.
/// Concurrency assumption (documented per task): one attempt at a time per feed — the later single scheduler per instance guarantees it — so there is
/// no internal locking around the load-modify-write of <see cref="FeedState"/>; the type is still thread-safe as a singleton serving all instances/feeds.
/// </summary>
public sealed class FeedErrorTracker : IFeedErrorTracker
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);   // backoff base (T-016 decision 3)
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);       // backoff cap — exact, not "at least"

    private readonly IStateStore _store;
    private readonly ILogger<FeedErrorTracker> _logger;
    private readonly bool _enableAutoRemove;
    private readonly int _autoRemoveThreshold;

    /// <summary>Process-lifetime set of auto-removed feed URLs (T-016 decision 6). Ordinal: feed URLs are case-sensitive.</summary>
    private readonly ConcurrentDictionary<string, byte> _autoRemovedFeeds = new(StringComparer.Ordinal);
    private int _autoRemovedCount;   // written via Interlocked only — see AutoRemovedCount

    /// <param name="enableAutoRemove">Issue default true (T-016 decision 2) — the frozen T-002 schema has no config key for it.</param>
    /// <param name="autoRemoveThreshold">Issue default 10 (T-016 decision 2). Must be positive — validated fail-fast at construction.</param>
    public FeedErrorTracker(IStateStore store, ILogger<FeedErrorTracker> logger, bool enableAutoRemove = true, int autoRemoveThreshold = 10)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        if (autoRemoveThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(autoRemoveThreshold), autoRemoveThreshold, "The auto-remove threshold must be a positive number of failures.");

        _store = store;
        _logger = logger;
        _enableAutoRemove = enableAutoRemove;
        _autoRemoveThreshold = autoRemoveThreshold;
    }

    public bool IsAutoRemoved(string feedUrl) => _autoRemovedFeeds.ContainsKey(feedUrl);

    public int AutoRemovedCount => Volatile.Read(ref _autoRemovedCount);

    public async Task<FeedErrorDecision> RecordAttemptAsync(string feedUrl, FeedAttemptOutcome outcome, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feedUrl);
        ArgumentNullException.ThrowIfNull(outcome);

        var existing = await _store.GetFeedStateAsync(feedUrl, ct);
        var state = existing ?? new FeedState(feedUrl, null, null, null);   // absent ⇒ a fresh row (T-016 decision 4)

        if (outcome.Success)
        {
            // Recovery: reset the persisted counter. Auto-removal is sticky for the process lifetime — a success never un-removes a feed. The `with`
            // expression preserves every other field (Etag, LastFetchedAtUtc, LastItemPublishedAtUtc).
            await _store.UpsertFeedStateAsync(state with { ErrorCount = 0, LastErrorAtUtc = null }, ct);

            return new FeedErrorDecision(0, TimeSpan.Zero, false);
        }

        var errorCount = state.ErrorCount + 1;
        var nextAttemptDelay = ComputeBackoff(errorCount);   // the formula is pinned to the count AFTER incrementing (decision 3)

        await _store.UpsertFeedStateAsync(state with { ErrorCount = errorCount, LastErrorAtUtc = nowUtc }, ct);   // every other field preserved by `with`
        _logger.LogInformation("Feed error recorded: {FeedUrl} — attempt failed ({FailureReason}); error count {ErrorCount}, next attempt in {NextAttemptDelay}.",
            feedUrl, outcome.FailureReason ?? "unknown failure", errorCount, nextAttemptDelay);

        var autoRemoved = false;
        if (_enableAutoRemove && errorCount >= _autoRemoveThreshold)
        {
            // Marking is idempotent (sticky), but the Error log + counter fire on EVERY failure at or above the threshold — a flap-failing feed
            // re-logs after removal, which is acceptable v1.0 semantics (T-016 impact assessment).
            _autoRemovedFeeds.TryAdd(feedUrl, 0);
            Interlocked.Increment(ref _autoRemovedCount);

            _logger.LogError("Feed auto-removed: {FeedUrl} — error count {ErrorCount} reached threshold {Threshold}; the feed is skipped for the rest of this process run.",
                feedUrl, errorCount, _autoRemoveThreshold);
            autoRemoved = true;
        }

        return new FeedErrorDecision(errorCount, nextAttemptDelay, autoRemoved);
    }

    /// <summary>T-024 (additive, decision 7): the remaining backoff for this feed at <paramref name="nowUtc"/> — reuses the existing persisted state and
    /// formula: <c>LastErrorAtUtc + min(30 s × 2^(count−1), 6 h) − nowUtc</c>, clamped ≥ 0. Zero when there is no row (never failed) or the last attempt
    /// succeeded (RecordAttemptAsync resets ErrorCount to 0 and clears LastErrorAtUtc). Read-only: it never mutates state.</summary>
    public async Task<TimeSpan> BackoffRemainingAsync(string feedUrl, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feedUrl);

        var state = await _store.GetFeedStateAsync(feedUrl, ct);

        if (state is null || state.ErrorCount == 0 || state.LastErrorAtUtc is not { } lastErrorAtUtc)
            return TimeSpan.Zero;   // never failed, or the last attempt succeeded ⇒ no backoff in effect

        var backoffUntil = lastErrorAtUtc + ComputeBackoff(state.ErrorCount);   // same formula as RecordAttemptAsync (decision 3)
        return backoffUntil > nowUtc ? backoffUntil - nowUtc : TimeSpan.Zero;   // clamped ≥ 0 — the window has already elapsed ⇒ zero
    }

    /// <summary>Binding backoff formula (T-016 decision 3): min(30 s × 2^(count−1), 6 h). Pinned boundaries: count 1 → 30 s, …, 10 → 4 h 16 m
    /// (30 s × 2^9 = 15,360 s), ≥ 11 → exactly 6 h. The doubling stops once the cap is reached, so arbitrarily large counts neither overflow nor keep growing.</summary>
    private static TimeSpan ComputeBackoff(int errorCount)
    {
        var delay = BaseDelay;   // count == 1 → the base (30 s)

        for (var shift = 1; shift < errorCount && delay < MaxDelay; shift++)
            delay *= 2;          // count n → base × 2^(n−1), stopping once the cap is reached

        return delay > MaxDelay ? MaxDelay : delay;   // exact cap at 6 h (first crossed at count 11: 30 s × 2^10 = 8 h 32 m)
    }
}
