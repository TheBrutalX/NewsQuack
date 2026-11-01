using NewsQuack.Core;
using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NewsQuack.Infrastructure.Persistence;

/// <summary>
/// EF Core + SQLite implementation of the frozen <see cref="IStateStore"/> contract (T-015). One context per operation via the
/// factory, short transactions only where atomicity is required (<see cref="MarkDeliveredAsync"/>), 100% LINQ data access — the only
/// raw SQL in this project is the sanctioned connection-init PRAGMAs in <see cref="SqliteConnectionInitInterceptor"/>.
/// </summary>
public sealed class SqliteStateStore : IStateStore
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SqliteStateStore> _logger;

    public SqliteStateStore(IDbContextFactory<AppDbContext> contextFactory, ILogger<SqliteStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<FeedState?> GetFeedStateAsync(string feedUrl, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var row = await db.FeedStateRows.AsNoTracking().FirstOrDefaultAsync(r => r.FeedUrl == feedUrl, ct);
        return row is null
            ? null
            // Boundary conversion: persisted UTC DateTime → the contract's DateTimeOffset (zero offset).
            : new FeedState(row.FeedUrl, ToOffset(row.LastItemPublishedAtUtc), row.Etag, ToOffset(row.LastFetchedAtUtc), row.ErrorCount, ToOffset(row.LastErrorAtUtc));
    }

    public async Task UpsertFeedStateAsync(FeedState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var row = await LoadOrCreateFeedStateRowAsync(db, state, ct);
        ApplyFeedState(row, state);

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsDeliveredAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        // Delivery is per-instance: the same item may be delivered by multiple instances (contract semantics).
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        return await db.DeliveredItems.AsNoTracking().AnyAsync(d => d.InstanceId == instanceId && d.ItemId == itemId, ct);
    }

    public async Task MarkPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        // T-049: upsert — a re-mark (the next poll of a slow drain) is the NORMAL case, so load-or-create and refresh; it must never throw a unique violation.
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var row = await db.PendingItems.FirstOrDefaultAsync(p => p.InstanceId == instanceId && p.ItemId == itemId, ct);
        if (row is null)
        {
            row = new PendingItem { InstanceId = instanceId, ItemId = itemId };
            db.PendingItems.Add(row);
        }

        row.EnqueuedAtUtc = DateTimeOffset.UtcNow.UtcDateTime;   // boundary conversion: persisted as UTC DateTime (the contract passes no timestamp — the store stamps it)

        await db.SaveChangesAsync(ct);
    }

    public async Task ClearPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        // T-049: delete by key; a no-op when absent (the confirm/dead-letter transaction may have cleared the row already).
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var row = await db.PendingItems.FirstOrDefaultAsync(p => p.InstanceId == instanceId && p.ItemId == itemId, ct);
        if (row is null)
            return;

        db.PendingItems.Remove(row);   // untracked entity with key set — attached as Deleted, no raw SQL needed
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsPendingAsync(string instanceId, string itemId, CancellationToken ct = default)
    {
        // T-049: the in-flight half of the dedup check — one indexed lookup, per-instance by construction (same shape as IsDeliveredAsync).
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        return await db.PendingItems.AsNoTracking().AnyAsync(p => p.InstanceId == instanceId && p.ItemId == itemId, ct);
    }

    public async Task<int> CleanupAllPendingAsync(CancellationToken ct = default)
    {
        // T-049: startup cleanup — at boot every pending row belongs to the previous (dead) process; delete ALL of them before any worker starts.
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var rows = await db.PendingItems.AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
            return 0;

        db.PendingItems.RemoveRange(rows);   // untracked entities with keys set are attached as Deleted — no raw SQL needed
        await db.SaveChangesAsync(ct);

        return rows.Count;
    }

    public async Task MarkDeliveredAsync(string instanceId, string feedUrl, string itemId, DateTimeOffset deliveredAtUtc, FeedState? newFeedState, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        try
        {
            // ONE transaction: the delivered-item row and the feed-state advance commit in a single SaveChanges (binding contract atomicity).
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            db.DeliveredItems.Add(new DeliveredItem
            {
                InstanceId = instanceId,
                ItemId = itemId,
                FeedUrl = feedUrl,
                Title = null,   // the contract passes only id/feed/item/deliveredAt — no title is available here
                DeliveredAtUtc = deliveredAtUtc.UtcDateTime,   // boundary conversion: contract DateTimeOffset → persisted UTC DateTime
            });

            // T-049: clear the in-flight marker in THIS SAME transaction — the pending delete commits atomically with the delivered insert (one SaveChanges, no second round trip). A failed commit rolls both back.
            var pending = await db.PendingItems.FirstOrDefaultAsync(p => p.InstanceId == instanceId && p.ItemId == itemId, ct);
            if (pending is not null)
                db.PendingItems.Remove(pending);

            if (newFeedState is not null)
                ApplyFeedState(await LoadOrCreateFeedStateRowAsync(db, newFeedState, ct), newFeedState);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent MarkDelivered for the same (instanceId, itemId) won the unique-index race: it already recorded the delivery.
            // Log with the exception as argument and complete without throwing — this call's partial changes roll back with the transaction.
            _logger.LogInformation(ex, "Concurrent MarkDelivered for instance {InstanceId} item {ItemId}: unique constraint violation, treating as already delivered.", instanceId, itemId);
        }
    }

    public async Task<int> CleanupOlderThanAsync(DateTimeOffset cutoffUtc, int batchSize = 1000, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be positive.");

        // One context for the whole operation; removed entities detach after each SaveChanges, so the change tracker does not grow.
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var totalRemoved = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Oldest rows past the cutoff first — deterministic batch boundaries, pure LINQ (the cutoff is converted to UTC DateTime so the
            // SQLite provider can translate it). Each SaveChanges is a short transaction: WAL allows one writer at a time and Default Timeout=5
            // absorbs the wait for concurrent writers.
            var cutoffUtcAsDateTime = cutoffUtc.UtcDateTime;   // boundary conversion — DateTimeOffset is not translatable in WHERE/ORDER BY
            var batch = await db.DeliveredItems.AsNoTracking()
                .Where(d => d.DeliveredAtUtc < cutoffUtcAsDateTime)
                .OrderBy(d => d.DeliveredAtUtc)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            db.DeliveredItems.RemoveRange(batch);   // untracked entities with keys set are attached as Deleted — no raw SQL needed
            await db.SaveChangesAsync(ct);

            totalRemoved += batch.Count;
            if (batch.Count < batchSize)
                break;   // final partial batch — nothing older than the cutoff remains
        }

        return totalRemoved;
    }

    public async Task RecordDeadLetterAsync(DeadLetterData deadLetter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);

        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        // T-049: UPSERT — load-or-create exactly like LoadOrCreateFeedStateRowAsync. Repeated dead-lettering of the same item (re-attempted on a later
        // cycle — unchanged known limitation) refreshes the ONE row instead of duplicating it; the unique index on (instance_id, item_id) is the safety net.
        var entry = await db.DeadLetterEntries.FirstOrDefaultAsync(e => e.InstanceId == deadLetter.InstanceId && e.ItemId == deadLetter.ItemId, ct);
        if (entry is null)
        {
            entry = new DeadLetterEntry { InstanceId = deadLetter.InstanceId, FeedUrl = deadLetter.FeedUrl, ItemId = deadLetter.ItemId };
            db.DeadLetterEntries.Add(entry);
        }

        entry.PayloadJson = deadLetter.PayloadJson;   // full post payload as JSON — serialized by the caller (T-022)
        entry.Attempts = deadLetter.Attempts;
        entry.ErrorCategory = deadLetter.ErrorCategory;   // "429" / "5xx" / "timeout" / "400-payload" — classified by the caller (T-022)
        entry.LastError = deadLetter.LastError;
        entry.FailedAtUtc = deadLetter.FailedAtUtc.UtcDateTime;   // boundary conversion: contract DateTimeOffset → persisted UTC DateTime

        // T-049: clear the in-flight marker in the SAME SaveChanges — atomic with the dead-letter upsert (a separate best-effort clear after a failed
        // save would park the item pending until restart and change the documented re-attempt behavior).
        var pending = await db.PendingItems.FirstOrDefaultAsync(p => p.InstanceId == deadLetter.InstanceId && p.ItemId == deadLetter.ItemId, ct);
        if (pending is not null)
            db.PendingItems.Remove(pending);

        await db.SaveChangesAsync(ct);   // one save — upsert + marker clear commit together
    }

    private static async Task<FeedStateRow> LoadOrCreateFeedStateRowAsync(AppDbContext db, FeedState state, CancellationToken ct)
    {
        var row = await db.FeedStateRows.FirstOrDefaultAsync(r => r.FeedUrl == state.FeedUrl, ct);
        if (row is null)
        {
            row = new FeedStateRow { FeedUrl = state.FeedUrl };
            db.FeedStateRows.Add(row);
        }

        return row;
    }

    private static void ApplyFeedState(FeedStateRow row, FeedState state)
    {
        // Boundary conversion: the contract's DateTimeOffset → persisted UTC DateTime (null-safe).
        row.LastItemPublishedAtUtc = ToUtc(state.LastItemPublishedAtUtc);
        row.Etag = state.Etag;
        row.LastFetchedAtUtc = ToUtc(state.LastFetchedAtUtc);
        row.ErrorCount = state.ErrorCount;
        row.LastErrorAtUtc = ToUtc(state.LastErrorAtUtc);
    }

    private static DateTimeOffset? ToOffset(DateTime? utc) => utc is { } dt ? new DateTimeOffset(dt, TimeSpan.Zero) : null;

    private static DateTime? ToUtc(DateTimeOffset? offset) => offset?.UtcDateTime;

    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is SqliteException sqlite && sqlite.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT — the dedup unique index lost a concurrent race
                return true;

        return false;
    }
}
