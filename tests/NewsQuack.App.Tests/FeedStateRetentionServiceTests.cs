using NewsQuack.App.Services;
using NewsQuack.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;   // NullLogger<T> for the cancellation-path test (no log assertions there)
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-015 retention unit: the loop logic is invoked through the public RunCleanupOnceAsync test seam (no 24 h wait) with a fake IStateStore that
/// captures the cutoff/batch it was called with — verifying the 30-day cutoff, batch size 1000, and the structured start/finish logging. The
/// cancellation path is exercised through StartAsync/StopAsync around the 60 s initial delay (no sleep of more than a few ms in tests).
/// </summary>
public class FeedStateRetentionServiceTests
{
    private sealed class FakeStateStore : IStateStore
    {
        public DateTimeOffset? CutoffUtc { get; private set; }
        public int BatchSize { get; private set; }
        public int RowsToRemove { get; set; }

        public Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default) => Task.FromResult<FeedState?>(null);

        public Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default) => Task.FromResult(false);

        public Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default)
        {
            CutoffUtc = cutoffUtc;   // captured — the assertions below verify the service's constants end-to-end through the contract
            BatchSize = batchSize;

            return Task.FromResult(RowsToRemove);
        }

        public Task RecordDeadLetterAsync(DeadLetterData deadLetter, CancellationToken ct = default) => Task.CompletedTask;   // T-022: retention never touches dead letters (later SP)
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task RetentionService_RunCleanupOnce_CallsStoreWith30DayCutoffAndBatch1000()
    {
        var store = new FakeStateStore { RowsToRemove = 42 };
        var logger = new CapturingLogger<FeedStateRetentionService>();
        var service = new FeedStateRetentionService(store, logger);

        var before = DateTimeOffset.UtcNow;   // UTC — no local time in tests either
        await service.RunCleanupOnceAsync();
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(store.CutoffUtc);
        Assert.InRange(store.CutoffUtc!.Value, before.AddDays(-30), after.AddDays(-30));   // 30-day cutoff computed from UTC "now"
        Assert.Equal(1000, store.BatchSize);

        // Structured run logging: exactly an Information start + finish pair, the finish line carrying rows removed and duration.
        Assert.Equal(2, logger.Entries.Count);
        var (startLevel, startMessage) = logger.Entries[0];
        var (finishLevel, finishMessage) = logger.Entries[1];

        Assert.Equal(LogLevel.Information, startLevel);
        Assert.Contains("retention run started", startMessage, StringComparison.Ordinal);
        Assert.Contains(store.CutoffUtc.Value.ToString("O"), startMessage, StringComparison.Ordinal);   // cutoff in the log (o format)

        Assert.Equal(LogLevel.Information, finishLevel);
        Assert.Contains("retention run finished", finishMessage, StringComparison.Ordinal);
        Assert.Contains("42 rows removed", finishMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionService_HostStopsDuringInitialDelay_CompletesWithoutRunningCleanup()
    {
        var store = new FakeStateStore();
        var service = new FeedStateRetentionService(store, NullLogger<FeedStateRetentionService>.Instance);

        await service.StartAsync(CancellationToken.None);   // starts the background loop (60 s initial delay)
        await Task.Delay(100, CancellationToken.None);      // bounded settle wait — far below the 60 s delay
        await service.StopAsync(CancellationToken.None);    // cancels stoppingToken → ExecuteAsync returns via its OCE path

        Assert.Null(store.CutoffUtc);   // cleanup never ran — the host stopped during the initial delay
    }
}
