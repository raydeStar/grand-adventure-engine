using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiWorldPhase1Foundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_player_rooms_player_room",
                table: "player_rooms");

            migrationBuilder.DropPrimaryKey(
                name: "PK_combat_states",
                table: "combat_states");

            migrationBuilder.AddColumn<string>(
                name: "world_id",
                table: "story_entries",
                type: "text",
                nullable: false,
                defaultValue: "default-world");

            migrationBuilder.AddColumn<string>(
                name: "world_ids",
                table: "rooms",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[\"default-world\"]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "active_world_id",
                table: "players",
                type: "text",
                nullable: false,
                defaultValue: "default-world");

            migrationBuilder.AddColumn<string>(
                name: "home_world_id",
                table: "players",
                type: "text",
                nullable: false,
                defaultValue: "default-world");

            migrationBuilder.AddColumn<string>(
                name: "world_id",
                table: "player_rooms",
                type: "text",
                nullable: false,
                defaultValue: "default-world");

            migrationBuilder.AddColumn<string>(
                name: "world_id",
                table: "combat_states",
                type: "text",
                nullable: false,
                defaultValue: "default-world");

            migrationBuilder.AddPrimaryKey(
                name: "PK_combat_states",
                table: "combat_states",
                columns: new[] { "room_id", "world_id" });

            migrationBuilder.CreateTable(
                name: "player_world_states",
                columns: table => new
                {
                    player_id = table.Column<string>(type: "text", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    current_room_id = table.Column<string>(type: "text", nullable: false),
                    has_visited = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    first_visited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_visited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_world_states", x => new { x.player_id, x.world_id });
                });

            migrationBuilder.CreateTable(
                name: "stat_translation_history",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    source_world_id = table.Column<string>(type: "text", nullable: false),
                    destination_world_id = table.Column<string>(type: "text", nullable: false),
                    translated_stats = table.Column<string>(type: "jsonb", nullable: false),
                    translation_notes = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stat_translation_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "world_npc_states",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    npc_id = table.Column<string>(type: "text", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: true),
                    disposition_state = table.Column<string>(type: "jsonb", nullable: false),
                    knowledge_scope_overrides = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_world_npc_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "world_stat_snapshots",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    world_id = table.Column<string>(type: "text", nullable: false),
                    stats = table.Column<string>(type: "jsonb", nullable: false),
                    @class = table.Column<string>(name: "class", type: "text", nullable: true),
                    race = table.Column<string>(type: "text", nullable: true),
                    level = table.Column<int>(type: "integer", nullable: false),
                    hp = table.Column<int>(type: "integer", nullable: false),
                    max_hp = table.Column<int>(type: "integer", nullable: false),
                    mp = table.Column<int>(type: "integer", nullable: false),
                    max_mp = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_world_stat_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "worlds",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    spawn_room_id = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    rules = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tags = table.Column<string>(type: "jsonb", nullable: false),
                    portals = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worlds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_story_entries_world_id",
                table: "story_entries",
                column: "world_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_rooms_player_room",
                table: "player_rooms",
                columns: new[] { "player_id", "room_id", "world_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stat_translation_history_player_id_source_world_id_destinat~",
                table: "stat_translation_history",
                columns: new[] { "player_id", "source_world_id", "destination_world_id" });

            migrationBuilder.CreateIndex(
                name: "IX_world_npc_states_npc_id_world_id_player_id",
                table: "world_npc_states",
                columns: new[] { "npc_id", "world_id", "player_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_world_stat_snapshots_player_id_world_id",
                table: "world_stat_snapshots",
                columns: new[] { "player_id", "world_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_world_states");

            migrationBuilder.DropTable(
                name: "stat_translation_history");

            migrationBuilder.DropTable(
                name: "world_npc_states");

            migrationBuilder.DropTable(
                name: "world_stat_snapshots");

            migrationBuilder.DropTable(
                name: "worlds");

            migrationBuilder.DropIndex(
                name: "ix_story_entries_world_id",
                table: "story_entries");

            migrationBuilder.DropIndex(
                name: "ix_player_rooms_player_room",
                table: "player_rooms");

            migrationBuilder.DropPrimaryKey(
                name: "PK_combat_states",
                table: "combat_states");

            migrationBuilder.DropColumn(
                name: "world_id",
                table: "story_entries");

            migrationBuilder.DropColumn(
                name: "world_ids",
                table: "rooms");

            migrationBuilder.DropColumn(
                name: "active_world_id",
                table: "players");

            migrationBuilder.DropColumn(
                name: "home_world_id",
                table: "players");

            migrationBuilder.DropColumn(
                name: "world_id",
                table: "player_rooms");

            migrationBuilder.DropColumn(
                name: "world_id",
                table: "combat_states");

            migrationBuilder.AddPrimaryKey(
                name: "PK_combat_states",
                table: "combat_states",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_rooms_player_room",
                table: "player_rooms",
                columns: new[] { "player_id", "room_id" },
                unique: true);
        }
    }
}
