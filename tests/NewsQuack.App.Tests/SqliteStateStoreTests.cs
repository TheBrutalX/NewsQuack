using NewsQuack.Core;
using NewsQuack.Infrastructure.Persistence;
using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;   // PooledDbContextFactory / NonPooledDbContextFactory — the same factory types DI uses in App
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-015 store tests: each test gets a fresh temp SQLite file + its own factory/store (no shared state between tests). The factory is the same
/// PooledDbContextFactory type DI uses in App, with the same SqliteConnectionInitInterceptor — so these exercise the production connection path.
/// </summary>
public class SqliteStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);   // UTC — deterministic values

    private readonly string _dbPath;
    private readonly PooledDbContextFactory<AppDbContext> _factory;
    private readonly SqliteStateStore _store;

    public SqliteStateStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"newsquack-t015-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new SqliteConnectionInitInterceptor())   // same interceptor as the App wiring — PRAGMAs run on every EF-opened connection
            .Options;

        _factory = new PooledDbContextFactory<AppDbContext>(options);
        _store = new SqliteStateStore(_factory, NullLogger<SqliteStateStore>.Instance);
    }

    public void Dispose()
    {
        // PooledDbContextFactory is not disposable in EF 10 — pooled connections close on GC; on Linux the temp files can be deleted while open.
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _dbPath + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>Applies migration 001 — the same MigrateAsync call App makes at startup.</summary>
    private Task MigrateAsync(CancellationToken ct = default) => MigrateStoreViaFactoryAsync(_factory, ct);

    private static async Task MigrateStoreViaFactoryAsync(IDbContextFactory<AppDbContext> factory, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        await context.Database.MigrateAsync(ct);
    }

    [Fact]
    public async Task Migrate_FreshDatabase_CreatesAllTablesAndUniqueIndexes()
    {
        await MigrateAsync();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        var tables = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";   // schema inspection only — data access stays LINQ
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Equal(
            new[]
            {
                "__EFMigrationsHistory",   // EF's own bookkeeping tables — expected alongside the model tables (EF 10 also adds a lock table)
                "__EFMigrationsLock",
                "dead_letter_entries",
                "delivered_items",
                "feed_state_rows",
                "image_cache_entries",
                "pending_items",           // T-049: the in-flight marker table...
                "youtube_channel_cache_entries",
            },
            tables.OrderBy(t => t).ToArray());   // exactly the six snake_case tables (+ EF bookkeeping), nothing else

        // ...and T-049's unique indexes: one pending row and one dead letter per (instance, item).
        var uniqueIndexes = new List<string>();
        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL AND UPPER(sql) LIKE '%UNIQUE%';";   // schema inspection only — data access stays LINQ
        using var indexReader = await indexCommand.ExecuteReaderAsync();
        while (await indexReader.ReadAsync())
            uniqueIndexes.Add(indexReader.GetString(0));

        Assert.Contains("IX_pending_items_instance_id_item_id", uniqueIndexes);   // the marker's re-mark safety net...
        Assert.Contains("IX_dead_letter_entries_instance_id_item_id", uniqueIndexes);   // ...and the dead-letter upsert key (T-049)
    }

    [Fact]
    public async Task GetFeedState_NeverSeen_ReturnsNull()
    {
        await MigrateAsync();

        var state = await _store.GetFeedStateAsync("https://example.com/never-seen.xml");

        Assert.Null(state);
    }

    [Fact]
    public async Task UpsertFeedState_ThenGet_RoundTripsAllFields()
    {
        await MigrateAsync();

        var state = new FeedState(
            "https://example.com/feed.xml",
            FixedUtc.AddDays(-1),
            "etag-42",
            FixedUtc,
            ErrorCount: 3,
            LastErrorAtUtc: FixedUtc.AddMinutes(-5));

        await _store.UpsertFeedStateAsync(state);

        var read = await _store.GetFeedStateAsync(state.FeedUrl);

        Assert.NotNull(read);
        Assert.Equal(state.FeedUrl, read!.FeedUrl);
        Assert.Equal(state.LastItemPublishedAtUtc, read.LastItemPublishedAtUtc);
        Assert.Equal(state.Etag, read.Etag);
        Assert.Equal(state.LastFetchedAtUtc, read.LastFetchedAtUtc);
        Assert.Equal(state.ErrorCount, read.ErrorCount);
        Assert.Equal(state.LastErrorAtUtc, read.LastErrorAtUtc);

        // Upsert again with changed values — the same row is updated, not duplicated.
        await _store.UpsertFeedStateAsync(state with { Etag = "etag-43", ErrorCount = 0 });

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.FeedStateRows.CountAsync());   // still exactly one row for the feed URL
        Assert.Equal("etag-43", (await _store.GetFeedStateAsync(state.FeedUrl))!.Etag);
    }

    [Fact]
    public async Task IsDelivered_NotDelivered_ReturnsFalse()
    {
        await MigrateAsync();

        var delivered = await _store.IsDeliveredAsync("instance-1", "item-1");

        Assert.False(delivered);
    }

    [Fact]
    public async Task MarkDelivered_ThenIsDelivered_ReturnsTrue()
    {
        await MigrateAsync();

        await _store.MarkDeliveredAsync("instance-1", "https://example.com/feed.xml", "item-1", FixedUtc, null);

        Assert.True(await _store.IsDeliveredAsync("instance-1", "item-1"));
    }

    [Fact]
    public async Task MarkDelivered_AdvancesFeedStateAtomicallyWithItemInsert()
    {
        await MigrateAsync();

        const string feedUrl = "https://example.com/feed.xml";
        var newState = new FeedState(feedUrl, FixedUtc.AddDays(-2), "etag-7", FixedUtc.AddHours(1));

        // ONE call must leave BOTH rows consistent: the delivered item and the advanced feed state.
        await _store.MarkDeliveredAsync("instance-1", feedUrl, "item-1", FixedUtc, newState);

        Assert.True(await _store.IsDeliveredAsync("instance-1", "item-1"));

        var read = await _store.GetFeedStateAsync(feedUrl);
        Assert.NotNull(read);
        Assert.Equal(newState.LastItemPublishedAtUtc, read!.LastItemPublishedAtUtc);
        Assert.Equal(newState.Etag, read.Etag);
        Assert.Equal(newState.LastFetchedAtUtc, read.LastFetchedAtUtc);

        // The item row carries exactly the contract fields — Title stays null (the contract passes no title).
        await using var db = await _factory.CreateDbContextAsync();
        var item = await db.DeliveredItems.SingleAsync();
        Assert.Null(item.Title);
        Assert.Equal(FixedUtc.UtcDateTime, item.DeliveredAtUtc);   // persisted as UTC DateTime (boundary conversion)
    }

    [Fact]
    public async Task IsDelivered_SameItemIdDifferentInstance_ReturnsFalse()
    {
        await MigrateAsync();

        const string feedUrl = "https://example.com/feed.xml";
        await _store.MarkDeliveredAsync("instance-1", feedUrl, "item-1", FixedUtc, null);

        Assert.True(await _store.IsDeliveredAsync("instance-1", "item-1"));
        Assert.False(await _store.IsDeliveredAsync("instance-2", "item-1"));   // per-instance semantics: the other instance may still deliver it
    }

    [Fact]
    public async Task MarkDelivered_ConcurrentDuplicateSecondCall_DoesNotThrowAndStaysConsistent()
    {
        await MigrateAsync();

        const string feedUrl = "https://example.com/feed.xml";

        // Two tasks racing on the same (instanceId, itemId): the unique index makes exactly one of them win.
        var first = _store.MarkDeliveredAsync("instance-1", feedUrl, "item-1", FixedUtc, null);
        var second = _store.MarkDeliveredAsync("instance-1", feedUrl, "item-1", FixedUtc, null);

        await Task.WhenAll(first, second);   // both complete — the loser of the race must not throw

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.DeliveredItems.CountAsync());   // exactly one row survived
        Assert.True(await _store.IsDeliveredAsync("instance-1", "item-1"));
    }

    [Fact]
    public async Task MarkPending_ThenConfirm_PendingRowRemovedAtomicallyWithDelivered()
    {
        await MigrateAsync();

        const string feedUrl = "https://example.com/feed.xml";

        // T-049: the send stage marks the item in-flight BEFORE enqueueing...
        await _store.MarkPendingAsync("instance-1", "item-1");
        Assert.True(await _store.IsPendingAsync("instance-1", "item-1"));   // ...the marker is observable (dedup would now skip it)

        // ...and the confirm clears it in the SAME commit as the delivered insert.
        await _store.MarkDeliveredAsync("instance-1", feedUrl, "item-1", FixedUtc, null);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.DeliveredItems.CountAsync());   // one delivered row...
        Assert.Equal(0, await db.PendingItems.CountAsync());     // ...AND no pending row — the marker was cleared atomically with the confirm
        Assert.False(await _store.IsPendingAsync("instance-1", "item-1"));
    }

    [Fact]
    public async Task MarkDelivered_DuplicateCommitFails_PendingRowSurvivesRollback()
    {
        await MigrateAsync();

        const string feedUrl = "https://example.com/feed.xml";

        // Pre-seed the delivered row for this (instance, item): the confirm's INSERT will hit the unique index and fail...
        await using (var seedDb = await _factory.CreateDbContextAsync())
        {
            seedDb.DeliveredItems.Add(new DeliveredItem
            {
                InstanceId = "instance-1", ItemId = "item-1", FeedUrl = feedUrl, DeliveredAtUtc = FixedUtc.UtcDateTime,
            });

            await seedDb.SaveChangesAsync();
        }

        // ...so the WHOLE confirm transaction must roll back — including the pending clear (T-049: both land in one commit).
        await _store.MarkPendingAsync("instance-1", "item-1");
        await _store.MarkDeliveredAsync("instance-1", feedUrl, "item-1", FixedUtc, null);   // swallows the unique violation (concurrent-duplicate path)

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.PendingItems.CountAsync());   // the clear rolled back WITH the failed commit — a separate earlier clear would have left zero rows
        Assert.True(await _store.IsPendingAsync("instance-1", "item-1"));
    }

    [Fact]
    public async Task MarkPending_Twice_RefreshesEnqueuedAtUtcNoDuplicateRow()
    {
        await MigrateAsync();

        // T-049: a re-mark is the NORMAL case (the next poll of a slow drain) — it must simply succeed, never a unique violation.
        await _store.MarkPendingAsync("instance-1", "item-1");

        DateTime firstMark;
        await using (var probeDb = await _factory.CreateDbContextAsync())   // a separate context — the store's own contexts are already closed here
            firstMark = (await probeDb.PendingItems.SingleAsync()).EnqueuedAtUtc;

        await Task.Delay(20, CancellationToken.None);   // ensure a strictly later UtcNow tick — the contract passes no timestamp, the store stamps it

        await _store.MarkPendingAsync("instance-1", "item-1");   // second mark for the SAME (instanceId, itemId)

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.PendingItems.ToListAsync();
        Assert.Single(rows);                              // exactly one row — upsert, never a duplicate...
        Assert.True(rows[0].EnqueuedAtUtc > firstMark);   // ...and EnqueuedAtUtc was REFRESHED (a no-op second mark would keep the old instant)
    }

    [Fact]
    public async Task RecordDeadLetter_PersistsAllFields_RoundTripsThroughSqlite()
    {
        await MigrateAsync();   // T-022: the dead_letter_entries table comes from T-015's InitialCreate — no new migration (decision 1)

        var data = new DeadLetterData(
            "instance-1", "https://example.com/feed.xml", "item-9",
            """{"ItemId":"item-9","Title":"Broken post"}""",   // the full post payload as JSON — the store persists it verbatim
            5, "429", "Discord rate limit", FixedUtc);

        await _store.RecordDeadLetterAsync(data);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.DeadLetterEntries.SingleAsync();

        Assert.True(row.Id >= 1);   // rowid assigned by SQLite on insert
        Assert.Equal("instance-1", row.InstanceId);
        Assert.Equal("https://example.com/feed.xml", row.FeedUrl);
        Assert.Equal("item-9", row.ItemId);
        Assert.Equal(data.PayloadJson, row.PayloadJson);   // the full payload round-trips byte-for-byte...
        Assert.Equal(5, row.Attempts);                     // ...every field including the classified category and the last error...
        Assert.Equal("429", row.ErrorCategory);
        Assert.Equal("Discord rate limit", row.LastError);
        Assert.Equal(FixedUtc.UtcDateTime, row.FailedAtUtc);   // UTC boundary conversion: contract DateTimeOffset → persisted UTC DateTime (T-015 style)
    }

    [Fact]
    public async Task RecordDeadLetter_Twice_SingleRowUpdated()
    {
        await MigrateAsync();

        const string feedUrl = "https://example.com/feed.xml";

        // T-049: the same item dead-lettered again (re-attempted on a later cycle — the unchanged known limitation) must REFRESH the one row...
        var first = new DeadLetterData(
            "instance-1", feedUrl, "item-9",
            """{"ItemId":"item-9","Title":"first attempt"}""", 3, "5xx", "HTTP 503", FixedUtc);
        var second = new DeadLetterData(
            "instance-1", feedUrl, "item-9",
            """{"ItemId":"item-9","Title":"second attempt"}""", 5, "429", "Discord rate limit", FixedUtc.AddMinutes(10));

        await _store.RecordDeadLetterAsync(first);
        await _store.RecordDeadLetterAsync(second);   // ...instead of accumulating a duplicate row (the old blind insert piled up 52 rows in the smoke test)

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.DeadLetterEntries.ToListAsync();
        Assert.Single(rows);   // ONE row per (instance, item) — the unique index + load-or-create upsert...
        Assert.Equal(second.PayloadJson, rows[0].PayloadJson);            // ...refreshed with the latest payload...
        Assert.Equal(5, rows[0].Attempts);                                // ...attempts...
        Assert.Equal("429", rows[0].ErrorCategory);                       // ...category...
        Assert.Equal("Discord rate limit", rows[0].LastError);            // ...last error...
        Assert.Equal(second.FailedAtUtc.UtcDateTime, rows[0].FailedAtUtc);   // ...and the failed-at instant (upsert, not duplicate)

        // T-049: the dead-letter save also cleared any pending marker for the item — in the SAME SaveChanges as the upsert.
        await _store.MarkPendingAsync("instance-1", "item-9");
        await _store.RecordDeadLetterAsync(second);
        Assert.Equal(0, await db.PendingItems.CountAsync());   // no orphaned marker left behind (the item is retried next cycle, not parked)
    }

    [Fact]
    public async Task CleanupAllPending_RemovesEveryRow_ReturnsRemovedCount()
    {
        await MigrateAsync();

        // T-049: startup cleanup is GLOBAL — every instance's stale markers go (the rows belonged to a dead process, not one instance).
        await _store.MarkPendingAsync("instance-1", "item-1");
        await _store.MarkPendingAsync("instance-1", "item-2");
        await _store.MarkPendingAsync("instance-2", "item-1");

        var removed = await _store.CleanupAllPendingAsync();

        Assert.Equal(3, removed);   // the returned count is the rows actually removed...
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PendingItems.CountAsync());   // ...and every row of every instance is gone

        Assert.Equal(0, await _store.CleanupAllPendingAsync());   // a second run (nothing pending) is a clean no-op
    }

    [Fact]
    public async Task Migrate_PreExistingDatabase_AddsPendingTableAndDeadLetterUniqueIndexKeepingRows()
    {
        // Step 1: apply ONLY the original migration — a database exactly as left by a pre-T-049 process (EF 10's MigrateAsync takes one migration id).
        await using (var context = await _factory.CreateDbContextAsync())
            await context.Database.MigrateAsync("20261023091207_InitialCreate");

        // Seed rows the old schema had — they must survive the upgrade untouched.
        await using (var seedDb = await _factory.CreateDbContextAsync())
        {
            seedDb.DeliveredItems.Add(new DeliveredItem
            {
                InstanceId = "instance-1", ItemId = "item-old", FeedUrl = "https://example.com/feed.xml", DeliveredAtUtc = FixedUtc.UtcDateTime,
            });

            seedDb.DeadLetterEntries.Add(new DeadLetterEntry
            {
                InstanceId = "instance-1", FeedUrl = "https://example.com/feed.xml", ItemId = "item-dl",
                PayloadJson = "{}", Attempts = 5, ErrorCategory = "429", FailedAtUtc = FixedUtc.UtcDateTime,
            });

            await seedDb.SaveChangesAsync();
        }

        // Step 2: the full migration set (what startup runs) — must add the table + index without touching existing rows.
        await MigrateAsync();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        var tables = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";   // schema inspection only — data access stays LINQ
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Contains("pending_items", tables);   // the new table exists after the upgrade...

        var uniqueIndexes = new List<string>();
        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL AND UPPER(sql) LIKE '%UNIQUE%';";   // schema inspection only — data access stays LINQ
        using var indexReader = await indexCommand.ExecuteReaderAsync();
        while (await indexReader.ReadAsync())
            uniqueIndexes.Add(indexReader.GetString(0));

        Assert.Contains("IX_dead_letter_entries_instance_id_item_id", uniqueIndexes);   // ...and the pre-existing dead-letter table gained its unique index

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.DeliveredItems.CountAsync());                 // existing rows intact...
        Assert.Equal("item-old", (await db.DeliveredItems.SingleAsync()).ItemId);
        Assert.Equal(1, await db.DeadLetterEntries.CountAsync());             // ...including the pre-unique-index dead letter row...
        Assert.Equal("item-dl", (await db.DeadLetterEntries.SingleAsync()).ItemId);
        Assert.Equal(0, await db.PendingItems.CountAsync());                  // and the new table starts empty
    }

    [Fact]
    public async Task CleanupOlderThan_RemovesOnlyOldRowsAcrossMultipleBatches()
    {
        await MigrateAsync();

        const int oldCount = 2500;   // > 2×batchSize → forces multiple batches (1000 + 1000 + 500)
        const int newCount = 50;

        var cutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);   // UTC — contract-level value passed to the store
        var oldBase = cutoff.AddDays(-45).UtcDateTime;   // clearly older than the cutoff (entity level: UTC DateTime)
        var newBase = cutoff.AddDays(1).UtcDateTime;     // clearly newer

        await using (var seedDb = await _factory.CreateDbContextAsync())
        {
            for (var i = 0; i < oldCount + newCount; i++)
            {
                var isOld = i < oldCount;
                seedDb.DeliveredItems.Add(new DeliveredItem
                {
                    InstanceId = "instance-1",
                    ItemId = $"item-{i}",
                    FeedUrl = "https://example.com/feed.xml",
                    DeliveredAtUtc = (isOld ? oldBase : newBase).AddSeconds(i),   // unique timestamps → deterministic oldest-first batches
                });
            }

            await seedDb.SaveChangesAsync();
        }

        var removed = await _store.CleanupOlderThanAsync(cutoff, batchSize: 1000);

        Assert.Equal(oldCount, removed);   // exactly the old rows — across three batches (1000 + 1000 + 500)

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(newCount, await db.DeliveredItems.CountAsync());   // new rows survive
        Assert.All(await db.DeliveredItems.ToListAsync(), d => Assert.True(d.DeliveredAtUtc >= cutoff.UtcDateTime));
    }

    [Fact]
    public async Task Connection_WalModeEnabled_AfterStoreInit()
    {
        await MigrateAsync();                          // EF opens a connection → the interceptor runs the PRAGMAs on open
        await _store.GetFeedStateAsync("https://example.com/feed.xml");   // and normal store traffic goes through the same path

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";   // WAL persists per file — any connection sees it
        Assert.Equal("wal", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Connection_SynchronousNormal_OnNewConnections()
    {
        await MigrateAsync();   // schema must exist before the fresh connection queries it

        // EF closes SQLite connections after each operation, so every subsequent open is a brand-new physical connection — "new connection via
        // factory". An explicit transaction makes EF open the connection (the interceptor runs on that open) and hold it, so we can inspect
        // the live connection.
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var connection = db.Database.GetDbConnection();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";   // NORMAL == 1 (FULL would be 2)
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        await tx.CommitAsync();
    }
}
