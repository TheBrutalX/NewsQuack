using NewsQuack.Infrastructure.Persistence;
using NewsQuack.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NewsQuack.App.Tests;

public class AppDbContextTests
{
    [Fact]
    public void AppDbContext_TempSqlite_CreatesAllFiveTables()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"newsquack-t001-{Guid.NewGuid():N}.db");

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
            }

            var tables = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            Assert.Equal(
                new[]
                {
                    "dead_letter_entries",
                    "delivered_items",
                    "feed_state_rows",
                    "image_cache_entries",
                    "youtube_channel_cache_entries",
                },
                tables.OrderBy(t => t).ToArray());   // exactly the five snake_case tables, nothing else
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    [Fact]
    public void AppDbContext_Model_HasUniqueIndexOnInstanceIdAndItemId()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")   // never opened — model inspection only
            .Options;

        using var context = new AppDbContext(options);

        var found = context.Model.FindEntityType(typeof(DeliveredItem));
        Assert.NotNull(found);

        // Non-null: Assert.NotNull above fails the test otherwise (xUnit does not annotate its return type).
        var index = found!.GetIndexes().Single(i =>
            i.Properties.Count() == 2
            && i.Properties[0].Name == nameof(DeliveredItem.InstanceId)
            && i.Properties[1].Name == nameof(DeliveredItem.ItemId));

        Assert.True(index.IsUnique);
    }
}
