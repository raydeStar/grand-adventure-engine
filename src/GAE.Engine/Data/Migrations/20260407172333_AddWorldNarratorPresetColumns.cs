using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldNarratorPresetColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_narrator_preset_id",
                table: "worlds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "narrator_preset_ids",
                table: "worlds",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_narrator_preset_id",
                table: "worlds");

            migrationBuilder.DropColumn(
                name: "narrator_preset_ids",
                table: "worlds");
        }
    }
}
