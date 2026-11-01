using NewsQuack.Infrastructure.Persistence;   // AppDbContext — the factory-created context this service checkpoints through (T-015)
using Microsoft.EntityFrameworkCore;   // IDbContextFactory<AppDbContext> — one context per operation, exactly like every other store consumer
using Microsoft.Extensions.Hosting;   // IHostedService (plain SDK: no implicit MS.Ext usings in this project)
using Microsoft.Extensions.Logging;   // ILogger<T>

namespace NewsQuack.App.Services;

/// <summary>
/// T-031 decision 3: WAL checkpoint on graceful shutdown. A plain <see cref="IHostedService"/> with a no-op start — ALL work happens in
/// <see cref="StopAsync"/>, and it is registered inside AddStateStore BEFORE the retention service, so the host (which stops hosted services
/// sequentially in REVERSE registration order) runs it LAST: after the schedulers stopped and the send workers drained their queues. Opens a
/// factory-created context's connection through EF (so <c>SqliteConnectionInitInterceptor</c> applies WAL/synchronous exactly as on every other
/// open) and executes <c>PRAGMA wal_checkpoint(TRUNCATE)</c> — the SECOND sanctioned raw-SQL exception in this codebase (connection-level
/// maintenance, same category as T-015's init interceptor; no data access). The result row (busy / log / checkpointed frame counts) is read for a
/// structured Info log. Every exception is caught and logged Error with the exception as argument and NOT rethrown — a failed checkpoint must not
/// crash host stop. No sync-over-async anywhere in this path (T-031 decision 6).
/// </summary>
public sealed class StoreCheckpointService : IHostedService
{
    /// <summary>The shutdown checkpoint — TRUNCATE mode: on success the WAL file is truncated to zero bytes, so on-disk state is complete after stop.</summary>
    private const string CheckpointPragma = "PRAGMA wal_checkpoint(TRUNCATE);";

    /// <summary>Bounded deadline for the checkpoint work itself (issue #48): the stopping token handed to StopAsync is already canceled by the time we run, so a wedged SQLite connection must not hang host stop forever — 10 s is generous for a TRUNCATE on our data volumes.</summary>
    private const int CheckpointTimeoutSeconds = 10;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<StoreCheckpointService> _logger;

    public StoreCheckpointService(IDbContextFactory<AppDbContext> contextFactory, ILogger<StoreCheckpointService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>No-op start (decision 3): the checkpoint is a shutdown concern only — nothing to do when the host starts.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The whole job: factory-created context → open the underlying connection through EF → wal_checkpoint(TRUNCATE) → read the result row
    /// for a structured Info log. Catches everything (Error log, exception as argument) and never rethrows — host stop must survive a failed checkpoint.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // The stopping token the host hands us is ALREADY canceled by the time StopAsync runs — using it would cancel the very work we exist to do
        // (issue #48, found in the integration smoke test: "WAL checkpoint on shutdown failed: A task was canceled"). Bound the checkpoint with our
        // own deadline instead; a wedged connection gives up after CheckpointTimeoutSeconds rather than hanging host stop.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CheckpointTimeoutSeconds));
        var ct = cts.Token;

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(ct);   // factory-created context — one per operation, never shared

            await context.Database.OpenConnectionAsync(ct);   // through EF ⇒ the init interceptor applies WAL/synchronous as on every other open

            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = CheckpointPragma;   // connection-level maintenance — the second sanctioned raw-SQL exception (no data access)

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))   // the pragma returns exactly one row: busy | log | checkpointed
                _logger.LogInformation("WAL checkpoint on shutdown completed: busy={Busy}, wal-frames={LogFrames}, checkpointed-frames={CheckpointedFrames}", reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            else
                _logger.LogWarning("WAL checkpoint on shutdown returned no result row — nothing to report");   // defensive: the pragma always returns a row, but never assume it
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAL checkpoint on shutdown failed: {Message}", ex.Message);   // structured log — exception as argument, stack trace survives; NOT rethrown (decision 3)
        }
    }

    /// <summary>No resources of its own to release — contexts are created and disposed per operation inside StopAsync.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
