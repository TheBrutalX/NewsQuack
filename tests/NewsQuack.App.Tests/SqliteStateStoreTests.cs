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
    public async Task Migrate_FreshDatabase_CreatesAllFiveTables()
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
                "__EFMigrationsHistory",   // EF's own bookkeeping tables — expected alongside the five model tables (EF 10 also adds a lock table)
                "__EFMigrationsLock",
                "dead_letter_entries",
                "delivered_items",
                "feed_state_rows",
                "image_cache_entries",
                "youtube_channel_cache_entries",
            },
            tables.OrderBy(t => t).ToArray());   // exactly the five snake_case tables (+ EF bookkeeping), nothing else
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
