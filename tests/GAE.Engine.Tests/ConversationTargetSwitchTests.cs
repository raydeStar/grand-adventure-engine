using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Guards the "conversation is context, not custody" rule: a player mid-conversation with one NPC
/// must be able to address another present NPC in whatever words come naturally, while still being
/// able to discuss that NPC with their current partner.
/// </summary>
public class ConversationTargetSwitchTests
{
    private const string PlayerId = "switch-player";
    private const string Mara = "Mara the Barkeep";
    private const string Pete = "Stumbling Pete";

    /// <summary>
    /// Phrasings that address Pete directly. The earlier implementation matched a fixed list of
    /// literal phrases, so everything here except "talk to pete" was swallowed as dialogue by Mara.
    /// </summary>
    [Theory]
    [InlineData("talk to pete")]
    [InlineData("talk to Stumbling Pete")]
    [InlineData("speak with pete")]
    [InlineData("say hi to pete")]
    [InlineData("say hello to pete")]
    [InlineData("greet pete")]
    [InlineData("hey pete")]
    [InlineData("pete")]
    [InlineData("Pete, what did you see?")]
    [InlineData("Pete!")]
    [InlineData("ask pete about the back door")]
    [InlineData("turn to pete")]
    [InlineData("walk over to pete")]
    [InlineData("buy pete a drink")]
    [InlineData("offer pete a drink")]
    [InlineData("wave to pete")]
    [InlineData("sit down next to pete and ask about the door")]
    [InlineData("I walk over to Pete and offer to buy him a drink")]
    public async Task AddressingAnotherNpc_SwitchesConversation(string input)
    {
        var player = await RunFromMaraConversationAsync(input);

        Assert.Equal(InteractionMode.Conversation, player.Interaction.Mode);
        Assert.Equal(Pete, player.Interaction.Target);
    }

    /// <summary>
    /// Mentioning Pete while talking *to* Mara must not hijack the conversation — otherwise asking
    /// a barkeep about a regular would silently reassign the player.
    /// </summary>
    [Theory]
    [InlineData("what is wrong with pete")]
    [InlineData("ask mara about pete")]
    [InlineData("tell me about pete")]
    [InlineData("what do you think of pete")]
    [InlineData("who is pete")]
    [InlineData("pete is a liar")]
    [InlineData("does pete come here often")]
    [InlineData("tell me more")]
    public async Task MentioningAnotherNpc_StaysWithCurrentPartner(string input)
    {
        var player = await RunFromMaraConversationAsync(input);

        Assert.Equal(InteractionMode.Conversation, player.Interaction.Mode);
        Assert.Equal(Mara, player.Interaction.Target);
    }

    [Theory]
    [InlineData("goodbye")]
    [InlineData("leave")]
    [InlineData("stop talking to mara")]
    public async Task ExitPhrases_EndConversation(string input)
    {
        var player = await RunFromMaraConversationAsync(input);

        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
    }

    /// <summary>Enters a conversation with Mara, then issues <paramref name="input"/>.</summary>
    private static async Task<PlayerCharacter> RunFromMaraConversationAsync(string input)
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "The Rusted Flagon",
            Description = "A creaky tavern.",
            Npcs =
            [
                new Npc { Id = "mara", Name = Mara, Personality = "Guarded barkeep", Disposition = "neutral" },
                new Npc { Id = "pete", Name = Pete, Personality = "Nervous drunk", Disposition = "neutral" }
            ],
            Exits = new Dictionary<string, string> { ["east"] = "square" }
        });
        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId, Name = "Test Hero", Race = "Human", Class = "Warrior", Level = 5,
            CurrentRoomId = "tavern", Hp = 28, MaxHp = 28, Mp = 16, MaxMp = 19, Gold = 240,
            Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10
        });

        var engine = CreateEngine(stateManager, CreateNarrator().Object);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "talk to mara"));
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, input));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        return player;
    }

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string e, string p) => new DiceRoll { Expression = e, Purpose = p, IndividualRolls = [10], Total = 10 });
        dice.Setup(d => d.RollSkillCheck(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string s, int m) => new DiceRoll { Expression = "1d20", Purpose = s, IndividualRolls = [10], Modifier = m, Total = 10 + m });

        return new GameEngine(
            stateManager,
            dice.Object,
            narrator,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }

    private static Mock<INarratorService> CreateNarrator()
    {
        var narrator = new Mock<INarratorService>();

        narrator.Setup(s => s.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, Room _, Npc n, InteractionState _, string _, CancellationToken _) => new FreeFormResponse
            {
                Success = true,
                Narration = $"\"{n.Name} answers you.\"",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Conversation, NpcDisposition = "neutral" }
            });

        narrator.Setup(s => s.ProcessFreeFormAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "The world responds.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            });

        return narrator;
    }
}
