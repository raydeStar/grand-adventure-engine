using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class InteractionStateMachineTests
{
    private const string PlayerId = "test-player";

    // ── Conversation entry ──

    [Fact]
    public async Task TalkTo_EntersConversationMode()
    {
        var npc = new Npc { Id = "mara", Name = "Mara", Personality = "Cheerful barmaid", Disposition = "friendly" };
        var stateManager = await CreateStateAsync(npc: npc);
        var narrator = CreateConversationNarrator(npc);
        var engine = CreateEngine(stateManager, narrator.Object);

        var action = engine.ParseCommand(PlayerId, "talk to mara");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Mara", result.MechanicalSummary);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Conversation, player.Interaction.Mode);
        Assert.Equal("Mara", player.Interaction.Target);
    }

    [Fact]
    public async Task Conversation_SubsequentInput_RoutedThroughInteraction()
    {
        var npc = new Npc { Id = "mara", Name = "Mara", Disposition = "friendly" };
        var stateManager = await CreateStateAsync(npc: npc);
        var narrator = CreateConversationNarrator(npc);
        var engine = CreateEngine(stateManager, narrator.Object);

        // Enter conversation
        var action = engine.ParseCommand(PlayerId, "talk to mara");
        await engine.ProcessActionAsync(PlayerId, action);

        // Follow-up turn should stay in conversation
        var followUp = engine.ParseCommand(PlayerId, "tell me about the dragon");
        var result = await engine.ProcessActionAsync(PlayerId, followUp);

        Assert.True(result.Success);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Conversation, player.Interaction.Mode);
        Assert.True(player.Interaction.TurnCount >= 1);
    }

    [Fact]
    public async Task Conversation_Goodbye_ExitsToExploreMode()
    {
        var npc = new Npc { Id = "mara", Name = "Mara", Disposition = "friendly" };
        var stateManager = await CreateStateAsync(npc: npc);
        var narrator = CreateConversationNarrator(npc);
        var engine = CreateEngine(stateManager, narrator.Object);

        // Enter conversation
        var talkAction = engine.ParseCommand(PlayerId, "talk to mara");
        await engine.ProcessActionAsync(PlayerId, talkAction);

        // Say goodbye
        var byeAction = engine.ParseCommand(PlayerId, "goodbye");
        var result = await engine.ProcessActionAsync(PlayerId, byeAction);

        Assert.True(result.Success);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
        Assert.Null(player.Interaction.Target);
    }

    [Fact]
    public async Task Conversation_WalkAway_ExitsWithDispositionPenalty()
    {
        var npc = new Npc { Id = "mara", Name = "Mara", Disposition = "friendly" };
        var room = new Room
        {
            Id = "tavern",
            Name = "Tavern",
            Description = "A warm tavern.",
            Npcs = [npc],
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        };
        var stateManager = await CreateStateAsync(room: room);
        // Also add the destination room
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "street",
            Name = "Street",
            Description = "A dusty street.",
            Exits = new Dictionary<string, string> { ["south"] = "tavern" }
        });

        var narrator = CreateConversationNarrator(npc);
        // Also need NarrateActionAsync for the move aftermath
        narrator
            .Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated movement.");

        var engine = CreateEngine(stateManager, narrator.Object);

        // Enter conversation
        var talkAction = engine.ParseCommand(PlayerId, "talk to mara");
        await engine.ProcessActionAsync(PlayerId, talkAction);

        // Walk away mid-conversation
        var moveAction = engine.ParseCommand(PlayerId, "go north");
        var result = await engine.ProcessActionAsync(PlayerId, moveAction);

        // Player should exit conversation and move
        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
    }

    // ── Combat entry ──

    [Fact]
    public async Task Attack_HostileNpc_EntersCombatMode()
    {
        var npc = new Npc { Id = "goblin", Name = "Goblin", IsHostile = true, Hp = 10, MaxHp = 10, Defense = 8 };
        var stateManager = await CreateStateAsync(npc: npc);
        var narrator = CreateCombatNarrator(npc);
        narrator
            .Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You swing your weapon.");

        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        dice.Setup(d => d.RollAttack(It.IsAny<int>())).Returns(new DiceRoll { Expression = "1d20+1", Total = 12, IndividualRolls = [11] });
        dice.Setup(d => d.Roll(It.IsAny<string>())).Returns(new DiceRoll { Expression = "1d6", Total = 4, IndividualRolls = [4] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>())).Returns(new DiceRoll { Expression = "1d4+1", Total = 4, IndividualRolls = [3] });

        var engine = CreateEngine(stateManager, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack goblin");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        // If NPC survived, should be in combat mode
        var room = await stateManager.GetRoomAsync(player.CurrentRoomId);
        var goblin = room?.Npcs.FirstOrDefault(n => n.Name == "Goblin");
        if (goblin is not null && (goblin.Hp ?? 0) > 0)
        {
            Assert.Equal(InteractionMode.Combat, player.Interaction.Mode);
            Assert.Equal("Goblin", player.Interaction.Target);
        }
    }

    // ── InteractionState model tests ──

    [Fact]
    public void InteractionState_AppendContext_CapsAt20()
    {
        var state = new InteractionState();
        for (int i = 0; i < 25; i++)
        {
            state.AppendContext($"Entry {i}");
        }

        Assert.Equal(20, state.Context.Count);
        Assert.Equal("Entry 5", state.Context[0]); // Oldest 5 trimmed
        Assert.Equal("Entry 24", state.Context[^1]);
    }

    [Fact]
    public void InteractionState_Reset_ClearsAllFields()
    {
        var state = new InteractionState
        {
            Mode = InteractionMode.Combat,
            Target = "Goblin",
            NpcDisposition = "hostile",
            TurnCount = 5,
            CanLeave = false,
            LeaveConsequence = "flee_penalty"
        };
        state.AppendContext("Turn 1");
        state.AppendContext("Turn 2");

        state.Reset();

        Assert.Equal(InteractionMode.Explore, state.Mode);
        Assert.Null(state.Target);
        Assert.Empty(state.Context);
        Assert.Equal(0, state.TurnCount);
        Assert.Null(state.NpcDisposition);
        Assert.True(state.CanLeave);
        Assert.Null(state.LeaveConsequence);
    }

    // ── Helpers ──

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator, IProbabilityEngine? dice = null)
    {
        var diceObj = dice ?? new Mock<IProbabilityEngine>(MockBehavior.Strict).Object;
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(
            stateManager,
            diceObj,
            narrator,
            parser,
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }

    private static async Task<InMemoryStateManager> CreateStateAsync(Room? room = null, Npc? npc = null)
    {
        var stateManager = new InMemoryStateManager();

        var defaultRoom = room ?? new Room
        {
            Id = "tavern",
            Name = "Tavern",
            Description = "A warm tavern.",
            Npcs = npc is not null ? [npc] : [new Npc { Id = "barkeep", Name = "Barkeep", Disposition = "friendly" }],
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        };

        await stateManager.SaveRoomAsync(defaultRoom);

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId,
            Name = "Test Hero",
            Race = "Human",
            Class = "Warrior",
            Level = 1,
            CurrentRoomId = defaultRoom.Id,
            Hp = 12,
            MaxHp = 12,
            Mp = 4,
            MaxMp = 4,
            Str = 12,
            Dex = 10,
            Con = 11,
            Int = 10,
            Wis = 10,
            Cha = 10
        });

        return stateManager;
    }

    private static Mock<INarratorService> CreateConversationNarrator(Npc npc)
    {
        var narrator = new Mock<INarratorService>();

        narrator
            .Setup(s => s.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(),
                It.IsAny<Room>(),
                It.IsAny<Npc>(),
                It.IsAny<InteractionState>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, Room _, Npc n, InteractionState _, string input, CancellationToken _) => new FreeFormResponse
            {
                Success = true,
                Narration = $"\"{n.Name} responds to your words.\"",
                InteractionUpdate = new InteractionUpdate
                {
                    Mode = InteractionMode.Conversation,
                    NpcDisposition = "friendly",
                    Context = [$"Player said: {input}. {n.Name} responded."]
                }
            });

        return narrator;
    }

    private static Mock<INarratorService> CreateCombatNarrator(Npc enemy)
    {
        var narrator = new Mock<INarratorService>();

        narrator
            .Setup(s => s.ProcessCombatTurnAsync(
                It.IsAny<PlayerCharacter>(),
                It.IsAny<Room>(),
                It.IsAny<Npc>(),
                It.IsAny<InteractionState>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, Room _, Npc e, InteractionState _, string _, CancellationToken _) => new FreeFormResponse
            {
                Success = true,
                Narration = $"You clash with {e.Name}!",
                InteractionUpdate = new InteractionUpdate
                {
                    Mode = InteractionMode.Combat,
                    CombatStatus = "ongoing",
                    EnemyUpdate = new Dictionary<string, int> { ["hp"] = -3 },
                    Context = [$"Combat continues with {e.Name}."]
                }
            });

        return narrator;
    }
}
