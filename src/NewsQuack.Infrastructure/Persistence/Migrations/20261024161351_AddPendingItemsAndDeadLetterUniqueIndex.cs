using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewsQuack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingItemsAndDeadLetterUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    item_id = table.Column<string>(type: "TEXT", nullable: false),
                    enqueued_at_utc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dead_letter_entries_instance_id_item_id",
                table: "dead_letter_entries",
                columns: new[] { "instance_id", "item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_items_instance_id_item_id",
                table: "pending_items",
                columns: new[] { "instance_id", "item_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_items");

            migrationBuilder.DropIndex(
                name: "IX_dead_letter_entries_instance_id_item_id",
                table: "dead_letter_entries");
        }
    }
}
