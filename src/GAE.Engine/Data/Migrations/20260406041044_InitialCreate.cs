using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "combat_states",
                columns: table => new
                {
                    room_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_combat_states", x => x.room_id);
                });

            migrationBuilder.CreateTable(
                name: "conversation_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    log_id = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: true),
                    room_id = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: false),
                    system_prompt = table.Column<string>(type: "text", nullable: false),
                    user_prompt = table.Column<string>(type: "text", nullable: false),
                    response = table.Column<string>(type: "text", nullable: false),
                    temperature = table.Column<double>(type: "double precision", nullable: false),
                    max_tokens = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<string>(type: "text", nullable: false),
                    action_id = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    room_id = table.Column<string>(type: "text", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: false),
                    narration = table.Column<string>(type: "text", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "player_rooms",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    room_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    exits = table.Column<string>(type: "jsonb", nullable: false),
                    npcs = table.Column<string>(type: "jsonb", nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    environment_tags = table.Column<string>(type: "jsonb", nullable: false),
                    is_discovered = table.Column<bool>(type: "boolean", nullable: false),
                    ascii_art = table.Column<string>(type: "text", nullable: true),
                    discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_rooms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "players",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    race = table.Column<string>(type: "text", nullable: false),
                    @class = table.Column<string>(name: "class", type: "text", nullable: false),
                    backstory = table.Column<string>(type: "text", nullable: false),
                    discord_id = table.Column<string>(type: "text", nullable: true),
                    thread_id = table.Column<long>(type: "bigint", nullable: true),
                    has_completed_demo = table.Column<bool>(type: "boolean", nullable: false),
                    current_room_id = table.Column<string>(type: "text", nullable: false),
                    hp = table.Column<int>(type: "integer", nullable: false),
                    max_hp = table.Column<int>(type: "integer", nullable: false),
                    mp = table.Column<int>(type: "integer", nullable: false),
                    max_mp = table.Column<int>(type: "integer", nullable: false),
                    gold = table.Column<int>(type: "integer", nullable: false),
                    xp = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    str = table.Column<int>(type: "integer", nullable: false),
                    dex = table.Column<int>(type: "integer", nullable: false),
                    con = table.Column<int>(type: "integer", nullable: false),
                    @int = table.Column<int>(name: "int", type: "integer", nullable: false),
                    wis = table.Column<int>(type: "integer", nullable: false),
                    cha = table.Column<int>(type: "integer", nullable: false),
                    luck = table.Column<int>(type: "integer", nullable: false),
                    equipment = table.Column<string>(type: "jsonb", nullable: false),
                    inventory = table.Column<string>(type: "jsonb", nullable: false),
                    status_effects = table.Column<string>(type: "jsonb", nullable: false),
                    spellbook = table.Column<string>(type: "jsonb", nullable: false),
                    quest_log = table.Column<string>(type: "jsonb", nullable: false),
                    interaction = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_active_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_players", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    is_template = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    exits = table.Column<string>(type: "jsonb", nullable: false),
                    npcs = table.Column<string>(type: "jsonb", nullable: false),
                    items = table.Column<string>(type: "jsonb", nullable: false),
                    environment_tags = table.Column<string>(type: "jsonb", nullable: false),
                    is_discovered = table.Column<bool>(type: "boolean", nullable: false),
                    ascii_art = table.Column<string>(type: "text", nullable: true),
                    discovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rooms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "story_entries",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    action_id = table.Column<string>(type: "text", nullable: false),
                    raw_input = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    room_id = table.Column<string>(type: "text", nullable: false),
                    mechanical_summary = table.Column<string>(type: "text", nullable: false),
                    narration = table.Column<string>(type: "text", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_logs_player_id",
                table: "conversation_logs",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_logs_timestamp",
                table: "conversation_logs",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_game_events_created_at",
                table: "game_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_game_events_player_id",
                table: "game_events",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_game_events_type",
                table: "game_events",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_player_rooms_player_id",
                table: "player_rooms",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_rooms_player_room",
                table: "player_rooms",
                columns: new[] { "player_id", "room_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_players_current_room_id",
                table: "players",
                column: "current_room_id");

            migrationBuilder.CreateIndex(
                name: "ix_players_discord_id",
                table: "players",
                column: "discord_id",
                unique: true,
                filter: "discord_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rooms_is_template",
                table: "rooms",
                column: "is_template",
                filter: "is_template = true");

            migrationBuilder.CreateIndex(
                name: "ix_story_entries_player_id",
                table: "story_entries",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_entries_room_id",
                table: "story_entries",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_entries_timestamp",
                table: "story_entries",
                column: "timestamp",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "combat_states");

            migrationBuilder.DropTable(
                name: "conversation_logs");

            migrationBuilder.DropTable(
                name: "game_events");

            migrationBuilder.DropTable(
                name: "player_rooms");

            migrationBuilder.DropTable(
                name: "players");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "story_entries");
        }
    }
}
