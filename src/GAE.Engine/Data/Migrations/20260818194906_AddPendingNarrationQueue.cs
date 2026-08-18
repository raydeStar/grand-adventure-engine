using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingNarrationQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_narrations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    action_id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    room_id = table.Column<string>(type: "text", nullable: true),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    context_json = table.Column<string>(type: "jsonb", nullable: false),
                    placeholder_narration = table.Column<string>(type: "text", nullable: false),
                    narration = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_narrations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_narrations_action_id",
                table: "pending_narrations",
                column: "action_id");

            migrationBuilder.CreateIndex(
                name: "IX_pending_narrations_status_next_attempt_at_player_id_sequence",
                table: "pending_narrations",
                columns: new[] { "status", "next_attempt_at", "player_id", "sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_narrations");
        }
    }
}
