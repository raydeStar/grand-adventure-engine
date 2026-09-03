using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerCommandHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_command_holds",
                columns: table => new
                {
                    player_id = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    held_by = table.Column<string>(type: "text", nullable: false),
                    held_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_action_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_command_holds", x => x.player_id);
                    table.ForeignKey(
                        name: "FK_player_command_holds_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_player_command_holds_held_at",
                table: "player_command_holds",
                column: "held_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_command_holds");
        }
    }
}
