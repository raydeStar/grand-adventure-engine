using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerStoryModePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blind_adventure",
                table: "players",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cyoa_state",
                table: "players",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "game_mode",
                table: "players",
                type: "text",
                nullable: false,
                defaultValue: "FullRpg");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "blind_adventure",
                table: "players");

            migrationBuilder.DropColumn(
                name: "cyoa_state",
                table: "players");

            migrationBuilder.DropColumn(
                name: "game_mode",
                table: "players");
        }
    }
}
