using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GAE.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldCharacterCreationIntro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "character_creation_intro",
                table: "worlds",
                type: "text",
                nullable: true);

            // Set the Elarion intro for the default world
            migrationBuilder.Sql("""
                UPDATE worlds SET character_creation_intro =
                'The Aether parts, and a voice emerges — dry, amused, and older than the sandstone walls of Stonewake.

                *"Ah. Another soul stumbles through my door. I am the Narrator — the voice behind the curtain, the pen that writes your story as you live it. And what a story you''ve arrived for: the elemental Crystals that shield this world are fading, the Void presses against reality like dark water against glass, and Valeward needs heroes more than it needs another tavern regular."*

                *"But before we get to the saving-the-world part — who exactly am I narrating? Tell me about yourself."*

                *(Describe yourself however you like: "I''m a sneaky halfling who picks pockets" or "I''m a massive orc who solves problems with fists" — or just tell me your name and I''ll ask questions.)*'
                WHERE id = 'default-world';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "character_creation_intro",
                table: "worlds");
        }
    }
}
