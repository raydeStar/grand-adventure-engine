using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerGender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gender",
                table: "players",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gender",
                table: "players");
        }
    }
}
