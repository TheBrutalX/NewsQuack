using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewsQuack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dead_letter_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    feed_url = table.Column<string>(type: "TEXT", nullable: false),
                    item_id = table.Column<string>(type: "TEXT", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    error_category = table.Column<string>(type: "TEXT", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", nullable: true),
                    failed_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letter_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "delivered_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    item_id = table.Column<string>(type: "TEXT", nullable: false),
                    feed_url = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    delivered_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivered_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feed_state_rows",
                columns: table => new
                {
                    feed_url = table.Column<string>(type: "TEXT", nullable: false),
                    last_item_published_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    etag = table.Column<string>(type: "TEXT", nullable: true),
                    last_fetched_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    error_count = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    last_error_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feed_state_rows", x => x.feed_url);
                });

            migrationBuilder.CreateTable(
                name: "image_cache_entries",
                columns: table => new
                {
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    resolved_url = table.Column<string>(type: "TEXT", nullable: true),
                    cached_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_cache_entries", x => x.url);
                });

            migrationBuilder.CreateTable(
                name: "youtube_channel_cache_entries",
                columns: table => new
                {
                    channel_url = table.Column<string>(type: "TEXT", nullable: false),
                    last_checked_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_video_published_at_utc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_youtube_channel_cache_entries", x => x.channel_url);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivered_items_instance_id_item_id",
                table: "delivered_items",
                columns: new[] { "instance_id", "item_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letter_entries");

            migrationBuilder.DropTable(
                name: "delivered_items");

            migrationBuilder.DropTable(
                name: "feed_state_rows");

            migrationBuilder.DropTable(
                name: "image_cache_entries");

            migrationBuilder.DropTable(
                name: "youtube_channel_cache_entries");
        }
    }
}
