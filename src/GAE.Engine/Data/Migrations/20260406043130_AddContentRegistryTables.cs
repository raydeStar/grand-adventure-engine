using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContentRegistryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_registry",
                columns: table => new
                {
                    content_type = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_registry", x => new { x.content_type, x.id });
                });

            migrationBuilder.CreateTable(
                name: "game_config",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_config", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_registry_type",
                table: "content_registry",
                column: "content_type");

            migrationBuilder.CreateIndex(
                name: "ix_content_registry_type_name",
                table: "content_registry",
                columns: new[] { "content_type", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_registry");

            migrationBuilder.DropTable(
                name: "game_config");
        }
    }
}
