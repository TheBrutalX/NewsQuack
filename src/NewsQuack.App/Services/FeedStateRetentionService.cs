using System.Diagnostics;   // Stopwatch for the per-run duration log (not a date — no UTC rule involved)
using NewsQuack.Core;
using Microsoft.Extensions.Hosting;   // BackgroundService (plain SDK: no implicit MS.Ext usings in this project)
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Services;

/// <summary>
/// Periodic retention job (T-015): every 24 h purges delivered-item rows older than the 30-day cutoff in batches of 1000, so a crash mid-run
/// loses at most one batch. Values are constants per the coordinator decision — no config keys invented. A failed run is logged and retried
/// on the next interval; it never kills the service.
/// </summary>
public sealed class FeedStateRetentionService : BackgroundService
{
    private const int RetentionDays = 30;   // default retention per plan — constant, no config key
    private const int BatchSize = 1000;     // rows per delete batch per plan — constant, no config key
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);   // let boot/migration settle before the first run
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IStateStore _store;
    private readonly ILogger<FeedStateRetentionService> _logger;

    public FeedStateRetentionService(IStateStore store, ILogger<FeedStateRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// One retention run (test seam — unit tests invoke this directly instead of waiting 24 h): computes the 30-day cutoff from UTC "now",
    /// calls the store with batch size 1000, and logs start/finish with the removed-row count and duration.
    /// </summary>
    public async Task RunCleanupOnceAsync(CancellationToken ct = default)
    {
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-RetentionDays);   // UTC only — no local time
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Feed state retention run started (cutoff {CutoffUtc:o}, batch size {BatchSize}).", cutoffUtc, BatchSize);

        var removed = await _store.CleanupOlderThanAsync(cutoffUtc, BatchSize, ct);

        _logger.LogInformation("Feed state retention run finished: {RowsRemoved} rows removed in {DurationMs} ms.", removed, stopwatch.ElapsedMilliseconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);   // short initial delay so boot/migration settles first
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;   // host stopped during the initial delay — normal shutdown, nothing to clean up yet
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // host stopped mid-run — normal shutdown
            }
            catch (Exception ex)
            {
                // A failed run must not kill the service: structured log with the exception as argument, retry on the next interval.
                _logger.LogError(ex, "Feed state retention run failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);   // every 24 h — token-supervised delay in a work loop
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // host stopped between runs — normal shutdown
            }
        }
    }
}
