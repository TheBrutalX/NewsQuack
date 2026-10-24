using NewsQuack.App.Services;
using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;   // wiring test only — the exact AddFeedErrorTracking call Program.cs makes
using Microsoft.Extensions.Logging;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-016 unit: the per-feed error tracker against a small in-memory IStateStore fake (no SQLite) plus LogCapture for log assertions. The clock is
/// always injected — fixed test instants — so no test can pass by reading the real time. Covers the pinned backoff boundaries (decision 3),
/// persistence/recovery semantics with upstream-field preservation (decision 4), sticky process-lifetime auto-removal + counter (decisions 4/5/6),
/// construction validation (decision 2), and the DI wiring defaults.
/// </summary>
public class FeedErrorTrackerTests
{
    private const string FeedUrl = "https://example.com/feed.xml";

    /// <summary>The fixed injected instant from the test plan — never a real clock.</summary>
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    /// <summary>In-memory IStateStore fake: one dictionary of FeedState rows keyed by feed URL (upsert = overwrite). Single-threaded tests — no
    /// locking needed.</summary>
    private sealed class FakeStateStore : IStateStore
    {
        private readonly Dictionary<string, FeedState> _rows = new(StringComparer.Ordinal);

        public void Seed(FeedState state) => _rows[state.FeedUrl] = state;

        public FeedState? GetRow(string feedUrl) => _rows.TryGetValue(feedUrl, out var row) ? row : null;

        public Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default)
            => Task.FromResult(GetRow(feedUrl));

        public Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default)
        {
            _rows[state.FeedUrl] = state;   // FeedState is an immutable record — the stored reference IS the upserted value
            return Task.CompletedTask;
        }

        public Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default) => Task.FromResult(false);

        public Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task RecordDeadLetterAsync(DeadLetterData deadLetter, CancellationToken ct = default) => Task.CompletedTask;   // T-022: the tracker never touches dead letters
    }

    private static (FeedErrorTracker Tracker, FakeStateStore Store, LogCapture Logs) CreateTracker(bool enableAutoRemove = true, int autoRemoveThreshold = 10)
    {
        var store = new FakeStateStore();
        var logs = new LogCapture();
        var tracker = new FeedErrorTracker(store, logs.For<FeedErrorTracker>(), enableAutoRemove, autoRemoveThreshold);

        return (tracker, store, logs);
    }

    private static FeedAttemptOutcome Failure(string reason = "HttpError 503") => new(false, reason);

    private static FeedAttemptOutcome Success() => new(true);

    /// <summary>Records n failures at the fixed instant and returns the last decision.</summary>
    private static async Task<FeedErrorDecision> FailTimes(FeedErrorTracker tracker, int times)
    {
        FeedErrorDecision decision = null!;   // times >= 1 in every call site — the loop always assigns before the return
        for (var i = 0; i < times; i++)
            decision = await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);

        return decision;
    }

    // ── Backoff boundaries (decision 3, pinned exactly) ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAttempt_FirstFailure_Delays30Seconds()
    {
        var (tracker, _, _) = CreateTracker();

        var decision = await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);

        Assert.Equal(1, decision.ErrorCount);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.NextAttemptDelay);   // 30 s × 2^0 — the base
        Assert.False(decision.AutoRemoved);
    }

    [Fact]
    public async Task RecordAttempt_SecondFailure_DelaysOneMinute()
    {
        var (tracker, _, _) = CreateTracker();

        var decision = await FailTimes(tracker, 2);

        Assert.Equal(2, decision.ErrorCount);
        Assert.Equal(TimeSpan.FromMinutes(1), decision.NextAttemptDelay);    // 30 s × 2^1 — pinned boundary
    }

    [Fact]
    public async Task RecordAttempt_TenthFailure_DelaysFourHoursSixteenMinutes()
    {
        var (tracker, _, _) = CreateTracker();

        var decision = await FailTimes(tracker, 10);

        Assert.Equal(10, decision.ErrorCount);
        // The binding formula min(30 s × 2^(count−1), 6 h) yields 30 s × 2^9 = 15,360 s = exactly 4 h 16 m at count 10 — the task file's
        // "4 h 20 m" was an arithmetic slip (corrected there in the same commit); pure doubling from count 9 (2 h 8 m) is preserved.
        Assert.Equal(TimeSpan.FromMinutes(256), decision.NextAttemptDelay);   // pinned boundary per the binding formula
    }

    [Fact]
    public async Task RecordAttempt_EleventhFailure_CappedAtSixHours()
    {
        var (tracker, _, _) = CreateTracker();

        var decision = await FailTimes(tracker, 11);

        Assert.Equal(11, decision.ErrorCount);
        Assert.Equal(TimeSpan.FromHours(6), decision.NextAttemptDelay);     // 30 s × 2^10 would be 8 h 32 m — capped at exactly 6 h
    }

    [Fact]
    public async Task RecordAttempt_FiftiethFailure_StaysCappedAtSixHours()
    {
        var (tracker, _, _) = CreateTracker();

        var decision = await FailTimes(tracker, 50);

        Assert.Equal(50, decision.ErrorCount);
        Assert.Equal(TimeSpan.FromHours(6), decision.NextAttemptDelay);     // cap holds for arbitrarily large counts — no overflow growth
    }

    // ── Persistence & recovery (decision 4) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAttempt_Failure_IncrementsPersistedErrorCountAndLastErrorAtInjectedInstant()
    {
        var (tracker, store, _) = CreateTracker();
        var earlier = new DateTimeOffset(2026, 10, 23, 11, 58, 47, TimeSpan.Zero);

        await tracker.RecordAttemptAsync(FeedUrl, Failure(), earlier);
        await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);

        var row = store.GetRow(FeedUrl)!;   // asserted through the fake store's read path — what the scheduler would see next time
        Assert.Equal(2, row.ErrorCount);
        Assert.Equal(NowUtc, row.LastErrorAtUtc);   // exactly the injected instant — no clock read inside the tracker
    }

    [Fact]
    public async Task RecordAttempt_NoStateRowYet_CreatesRowWithCountOne()
    {
        var (tracker, store, _) = CreateTracker();

        Assert.Null(store.GetRow(FeedUrl));   // no row before the first attempt

        var decision = await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);

        Assert.Equal(1, decision.ErrorCount);
        var row = store.GetRow(FeedUrl)!;     // a fresh row was created with every other field null
        Assert.Equal(FeedUrl, row.FeedUrl);
        Assert.Null(row.LastItemPublishedAtUtc);
        Assert.Null(row.Etag);
        Assert.Null(row.LastFetchedAtUtc);
        Assert.Equal(NowUtc, row.LastErrorAtUtc);
    }

    [Fact]
    public async Task RecordAttempt_SuccessAfterFailures_ResetsCounterAndClearsLastErrorAt()
    {
        var (tracker, store, _) = CreateTracker();

        await FailTimes(tracker, 3);
        Assert.Equal(3, store.GetRow(FeedUrl)!.ErrorCount);

        var decision = await tracker.RecordAttemptAsync(FeedUrl, Success(), NowUtc);

        Assert.Equal(0, decision.ErrorCount);
        Assert.Equal(TimeSpan.Zero, decision.NextAttemptDelay);   // 0 on success (decision 1)
        Assert.False(decision.AutoRemoved);
        var row = store.GetRow(FeedUrl)!;
        Assert.Equal(0, row.ErrorCount);
        Assert.Null(row.LastErrorAtUtc);
    }

    [Fact]
    public async Task RecordAttempt_UpstreamFieldsPreservedOnUpsert()
    {
        var (tracker, store, _) = CreateTracker();
        var published = new DateTimeOffset(2026, 10, 23, 8, 15, 30, TimeSpan.Zero);
        var fetched = new DateTimeOffset(2026, 10, 23, 11, 45, 0, TimeSpan.Zero);
        store.Seed(new FeedState(FeedUrl, published, "W/\"v9\"", fetched));

        await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);   // failure upsert
        Assert.Equal("W/\"v9\"", store.GetRow(FeedUrl)!.Etag);          // ...preserves the upstream fields...
        Assert.Equal(fetched, store.GetRow(FeedUrl)!.LastFetchedAtUtc);

        await tracker.RecordAttemptAsync(FeedUrl, Success(), NowUtc);   // ...and so does the success upsert

        var row = store.GetRow(FeedUrl)!;
        Assert.Equal(published, row.LastItemPublishedAtUtc);
        Assert.Equal("W/\"v9\"", row.Etag);
        Assert.Equal(fetched, row.LastFetchedAtUtc);
    }

    // ── Auto-remove (decisions 4/5/6) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAttempt_NinthFailure_DefaultThreshold_NotRemoved()
    {
        var (tracker, store, logs) = CreateTracker();   // defaults: enableAutoRemove=true, threshold=10

        var decision = await FailTimes(tracker, 9);

        Assert.Equal(9, decision.ErrorCount);
        Assert.False(decision.AutoRemoved);
        Assert.False(tracker.IsAutoRemoved(FeedUrl));
        Assert.Equal(0, tracker.AutoRemovedCount);
        Assert.Null(logs.Find(LogLevel.Error, FeedUrl));   // no Error log below the threshold
        Assert.Equal(9, logs.CountAt(LogLevel.Information));   // one Info line per failure increment (decision 5)
        Assert.NotNull(logs.Find(LogLevel.Information, FeedUrl));
        Assert.Equal(9, store.GetRow(FeedUrl)!.ErrorCount);   // the count keeps persisting even without removal
    }

    [Fact]
    public async Task RecordAttempt_TenthFailure_AutoRemovedWithErrorLogAndCounterIncremented()
    {
        var (tracker, _, logs) = CreateTracker();   // defaults: threshold=10

        var decision = await FailTimes(tracker, 10);

        Assert.Equal(10, decision.ErrorCount);
        Assert.True(decision.AutoRemoved);
        Assert.True(tracker.IsAutoRemoved(FeedUrl));
        Assert.Equal(1, tracker.AutoRemovedCount);
        // The Error log (decision 4) carries feedUrl + count + threshold — the structured template rendered with all three.
        var error = logs.Find(LogLevel.Error, FeedUrl, "error count 10", "threshold 10");
        Assert.NotNull(error);
    }

    [Fact]
    public async Task RecordAttempt_EnableAutoRemoveFalse_CountGrowsPastThresholdNeverRemoved()
    {
        var (tracker, store, logs) = CreateTracker(enableAutoRemove: false);

        var decision = await FailTimes(tracker, 12);   // well past the default threshold of 10

        Assert.Equal(12, decision.ErrorCount);
        Assert.False(decision.AutoRemoved);
        Assert.False(tracker.IsAutoRemoved(FeedUrl));
        Assert.Equal(0, tracker.AutoRemovedCount);
        Assert.Null(logs.Find(LogLevel.Error, FeedUrl));   // no Error log — auto-remove is disabled, not merely untriggered
        Assert.Equal(12, store.GetRow(FeedUrl)!.ErrorCount);   // the persisted count still grows past the threshold
    }

    [Fact]
    public async Task RecordAttempt_CustomThresholdThree_RemovedOnThirdFailure()
    {
        var (tracker, _, logs) = CreateTracker(autoRemoveThreshold: 3);

        Assert.False((await FailTimes(tracker, 2)).AutoRemoved);   // below the custom threshold

        var decision = await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);

        Assert.Equal(3, decision.ErrorCount);
        Assert.True(decision.AutoRemoved);                        // removed exactly AT the custom threshold (>= semantics)
        Assert.True(tracker.IsAutoRemoved(FeedUrl));
        Assert.Equal(1, tracker.AutoRemovedCount);
        Assert.NotNull(logs.Find(LogLevel.Error, FeedUrl, "error count 3", "threshold 3"));
    }

    [Fact]
    public async Task IsAutoRemoved_StickyAcrossSubsequentSuccesses()
    {
        var (tracker, store, _) = CreateTracker();   // defaults: threshold=10

        await FailTimes(tracker, 10);
        Assert.True(tracker.IsAutoRemoved(FeedUrl));

        for (var i = 0; i < 2; i++)   // two successes — the counter resets each time, but removal is sticky for the process lifetime
        {
            var decision = await tracker.RecordAttemptAsync(FeedUrl, Success(), NowUtc);

            Assert.Equal(0, decision.ErrorCount);
            Assert.False(decision.AutoRemoved);
            Assert.True(tracker.IsAutoRemoved(FeedUrl));   // a success never un-removes (decision 4)
        }

        Assert.Equal(0, store.GetRow(FeedUrl)!.ErrorCount);

        var afterReset = await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);   // one failure after the reset: count 1 < threshold
        Assert.False(afterReset.AutoRemoved);                                            // ...so no NEW removal event...
        Assert.True(tracker.IsAutoRemoved(FeedUrl));                                      // ...but the feed stays removed (sticky)
    }

    // ── Construction (decision 2) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_InvalidThreshold_ThrowsArgumentOutOfRange()
    {
        var store = new FakeStateStore();
        var logs = new LogCapture();

        var zero = Assert.Throws<ArgumentOutOfRangeException>(() => new FeedErrorTracker(store, logs.For<FeedErrorTracker>(), autoRemoveThreshold: 0));
        Assert.Equal("autoRemoveThreshold", zero.ParamName);   // fail fast at construction — never a broken tracker in the container

        var negative = Assert.Throws<ArgumentOutOfRangeException>(() => new FeedErrorTracker(store, logs.For<FeedErrorTracker>(), autoRemoveThreshold: -3));
        Assert.Equal("autoRemoveThreshold", negative.ParamName);
    }

    // ── DI wiring (decision 1/2) ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFeedErrorTracking_WiredAsInProgram_UsesIssueDefaultsTrueAndTen()
    {
        var store = new FakeStateStore();
        var logs = new LogCapture();

        // The exact wiring Program.cs uses on both host paths — the fake IStateStore stands in for SqliteStateStore (the tracker only sees the contract).
        var services = new ServiceCollection();
        services.AddSingleton<IStateStore>(store);   // under the contract — exactly what the tracker's factory resolves
        services.AddSingleton<ILogger<FeedErrorTracker>>(logs.For<FeedErrorTracker>());
        services.AddFeedErrorTracking();

        using var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IFeedErrorTracker>();   // the later scheduler resolves the interface, never the concrete type

        Assert.IsType<FeedErrorTracker>(tracker);

        for (var i = 1; i <= 9; i++)   // issue default threshold=10: nothing removed before it
            Assert.False((await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc)).AutoRemoved);

        var tenth = await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);
        Assert.True(tenth.AutoRemoved);   // issue default enableAutoRemove=true — removed exactly at the 10th failure
    }

    // ── BackoffRemainingAsync (T-024 additive, decision 7) ────────────────────────────────────────────────

    [Fact]
    public async Task BackoffRemaining_NeverFailed_ReturnsZero()
    {
        var (tracker, _, _) = CreateTracker();   // no attempts recorded — no row at all

        Assert.Equal(TimeSpan.Zero, await tracker.BackoffRemainingAsync(FeedUrl, NowUtc));   // zero when never failed
    }

    [Fact]
    public async Task BackoffRemaining_AfterFirstFailure_ReturnsFullWindowThatShrinksToZero()
    {
        var (tracker, _, _) = CreateTracker();

        await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);   // count 1 ⇒ a 30 s backoff starting at the injected instant

        Assert.Equal(TimeSpan.FromSeconds(30), await tracker.BackoffRemainingAsync(FeedUrl, NowUtc));            // exactly at the failure instant: the full window remains
        Assert.Equal(TimeSpan.FromSeconds(20), await tracker.BackoffRemainingAsync(FeedUrl, NowUtc.AddSeconds(10)));   // 10 s later ⇒ 20 s remain (shrinks with time)...
        Assert.Equal(TimeSpan.Zero, await tracker.BackoffRemainingAsync(FeedUrl, NowUtc.AddSeconds(31)));         // ...and clamps to zero once it has elapsed (never negative)
    }

    [Fact]
    public async Task BackoffRemaining_SuccessAfterFailure_ResetsToZero()
    {
        var (tracker, _, _) = CreateTracker();

        await tracker.RecordAttemptAsync(FeedUrl, Failure(), NowUtc);   // count 1 ⇒ in backoff
        Assert.Equal(TimeSpan.FromSeconds(30), await tracker.BackoffRemainingAsync(FeedUrl, NowUtc));

        await tracker.RecordAttemptAsync(FeedUrl, Success(), NowUtc);   // a success resets the counter and clears LastErrorAtUtc

        Assert.Equal(TimeSpan.Zero, await tracker.BackoffRemainingAsync(FeedUrl, NowUtc));   // last attempt succeeded ⇒ no backoff in effect
    }
}
