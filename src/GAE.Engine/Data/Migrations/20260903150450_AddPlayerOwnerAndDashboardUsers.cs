using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerOwnerAndDashboardUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner_id",
                table: "players",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dashboard_users",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    normalized_username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dashboard_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_players_owner_id",
                table: "players",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_dashboard_users_normalized_username",
                table: "dashboard_users",
                column: "normalized_username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dashboard_users");

            migrationBuilder.DropIndex(
                name: "IX_players_owner_id",
                table: "players");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "players");
        }
    }
}
