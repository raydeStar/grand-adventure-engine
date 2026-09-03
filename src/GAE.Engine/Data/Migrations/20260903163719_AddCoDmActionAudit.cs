using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoDmActionAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "co_dm_actions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    request_id = table.Column<string>(type: "text", nullable: false),
                    action_type = table.Column<string>(type: "text", nullable: false),
                    action_kind = table.Column<string>(type: "text", nullable: false),
                    target_player_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    rationale = table.Column<string>(type: "text", nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    approval_token_hash = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    proposed_by = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    result_summary = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_co_dm_actions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_co_dm_actions_proposed_by_request_id",
                table: "co_dm_actions",
                columns: new[] { "proposed_by", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_co_dm_actions_status_created_at",
                table: "co_dm_actions",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_co_dm_actions_target_player_id",
                table: "co_dm_actions",
                column: "target_player_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "co_dm_actions");
        }
    }
}
