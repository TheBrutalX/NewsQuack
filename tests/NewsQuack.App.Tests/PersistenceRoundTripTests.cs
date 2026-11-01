using NewsQuack.Infrastructure.Persistence;
using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// Persistence smoke test: saves one row of every entity into a temp SQLite database and reads it back —
/// keeps the per-assembly 70% coverage gate measured in this project honest for NewsQuack.Infrastructure.
/// </summary>
public class PersistenceRoundTripTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // T-015: entity date properties are UTC DateTime (the EF Core SQLite provider cannot translate DateTimeOffset in queries) — the persisted form of FixedUtc.
    private static readonly DateTime FixedUtcPersisted = FixedUtc.UtcDateTime;

    [Fact]
    public void Persistence_SaveAllSixEntities_RoundTripsValues()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"newsquack-t040-{Guid.NewGuid():N}.db");

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var context = new AppDbContext(options))
            {
                context.Database.EnsureCreated();   // no migrations yet in this task — schema comes straight from the model

                context.DeliveredItems.Add(new DeliveredItem
                {
                    InstanceId = "instance-1", ItemId = "item-1", FeedUrl = "https://example.com/feed.xml",
                    Title = "title", DeliveredAtUtc = FixedUtcPersisted,   // T-015: persisted as UTC DateTime
                });
                context.FeedStateRows.Add(new FeedStateRow
                {
                    FeedUrl = "https://example.com/feed.xml", LastItemPublishedAtUtc = FixedUtcPersisted, Etag = "etag-1",
                    LastFetchedAtUtc = FixedUtc.AddHours(-1).UtcDateTime, ErrorCount = 2, LastErrorAtUtc = FixedUtc.AddMinutes(-5).UtcDateTime,
                });
                context.YoutubeChannelCacheEntries.Add(new YoutubeChannelCacheEntry
                {
                    ChannelUrl = "https://www.youtube.com/channel/UCd9fKqLmNpRsTuVwXyZaBcD",
                    LastCheckedAtUtc = FixedUtcPersisted, LastVideoPublishedAtUtc = FixedUtc.AddDays(-1).UtcDateTime,
                });
                context.ImageCacheEntries.Add(new ImageCacheEntry
                {
                    Url = "https://example.com/img/original.png", ResolvedUrl = "https://cdn.example.com/img/resolved.png", CachedAtUtc = FixedUtcPersisted,
                });
                context.DeadLetterEntries.Add(new DeadLetterEntry
                {
                    InstanceId = "instance-1", FeedUrl = "https://example.com/feed.xml", ItemId = "item-2",
                    PayloadJson = "{\"itemId\":\"item-2\"}", Attempts = 5, ErrorCategory = "429", LastError = "rate limited", FailedAtUtc = FixedUtcPersisted,
                });
                context.PendingItems.Add(new PendingItem   // T-049: the in-flight marker — one row per enqueued-but-unconfirmed (instance, item)
                {
                    InstanceId = "instance-1", ItemId = "item-3", EnqueuedAtUtc = FixedUtcPersisted,   // T-049: persisted as UTC DateTime
                });

                context.SaveChanges();
            }

            using (var context = new AppDbContext(options))
            {
                var delivered = context.DeliveredItems.Single();
                Assert.True(delivered.Id >= 1);   // rowid assigned by SQLite on insert
                Assert.Equal("instance-1", delivered.InstanceId);
                Assert.Equal("item-1", delivered.ItemId);
                Assert.Equal(FixedUtcPersisted, delivered.DeliveredAtUtc);   // T-015: round-trips as UTC DateTime

                var feedState = context.FeedStateRows.Single();
                Assert.Equal("etag-1", feedState.Etag);
                Assert.Equal(2, feedState.ErrorCount);

                var channel = context.YoutubeChannelCacheEntries.Single();
                Assert.Equal(FixedUtc.AddDays(-1).UtcDateTime, channel.LastVideoPublishedAtUtc);   // T-015: round-trips as UTC DateTime

                var image = context.ImageCacheEntries.Single();
                Assert.Equal("https://cdn.example.com/img/resolved.png", image.ResolvedUrl);

                var deadLetter = context.DeadLetterEntries.Single();
                Assert.True(deadLetter.Id >= 1);   // rowid assigned by SQLite on insert
                Assert.Equal(5, deadLetter.Attempts);
                Assert.Equal("429", deadLetter.ErrorCategory);

                var pending = context.PendingItems.Single();   // T-049: the marker row round-trips...
                Assert.True(pending.Id >= 1);                  // ...with a rowid assigned by SQLite on insert...
                Assert.Equal("instance-1", pending.InstanceId);
                Assert.Equal(FixedUtcPersisted, pending.EnqueuedAtUtc);   // ...and EnqueuedAtUtc round-trips as UTC DateTime (T-049)
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }
}
