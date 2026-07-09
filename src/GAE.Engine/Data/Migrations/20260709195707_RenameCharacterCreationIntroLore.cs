using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameCharacterCreationIntroLore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE worlds
                SET character_creation_intro = replace(
                    replace(
                        replace(character_creation_intro, 'The Mist parts', 'The Aether parts'),
                        'Rabanastre', 'Stonewake'),
                    'Dalmasca', 'Valeward')
                WHERE id = 'default-world'
                  AND character_creation_intro LIKE '%Rabanastre%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE worlds
                SET character_creation_intro = replace(
                    replace(
                        replace(character_creation_intro, 'The Aether parts', 'The Mist parts'),
                        'Stonewake', 'Rabanastre'),
                    'Valeward', 'Dalmasca')
                WHERE id = 'default-world'
                  AND character_creation_intro LIKE '%Stonewake%';
                """);
        }
    }
}
