using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// A player turn is a read-modify-write over the whole PlayerCharacter, so overlapping turns for
/// the same player must not interleave — otherwise one turn's saved state overwrites the other's.
/// </summary>
public class PlayerTurnSerializationTests
{
    private const string PlayerId = "concurrent-player";

    [Fact]
    public async Task ConcurrentTurnsForSamePlayer_DoNotOverlap()
    {
        var stateManager = new InMemoryStateManager();
        await SeedAsync(stateManager);

        var concurrentTurns = 0;
        var maxConcurrentTurns = 0;
        var gate = new object();

        var narrator = new Mock<INarratorService>();
        narrator.Setup(s => s.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (gate)
                {
                    concurrentTurns++;
                    maxConcurrentTurns = Math.Max(maxConcurrentTurns, concurrentTurns);
                }

                // Hold the turn open long enough that unsynchronized callers would overlap.
                await Task.Delay(40);

                lock (gate) { concurrentTurns--; }

                return new FreeFormResponse
                {
                    Success = true,
                    Narration = "\"Mara answers you.\"",
                    InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Conversation, NpcDisposition = "neutral" }
                };
            });

        var engine = CreateEngine(stateManager, narrator.Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "talk to mara"));

        // Fire several turns at once, as a double-send or a dashboard+Discord overlap would.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, $"tell me secret {i}"))));

        Assert.Equal(1, maxConcurrentTurns);

        // Every turn must be recorded; a lost update would leave the count short.
        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(8, player.Interaction.PlayerTurnCount);
    }

    [Fact]
    public async Task ConcurrentTurnsForDifferentPlayers_StillRunInParallel()
    {
        var stateManager = new InMemoryStateManager();
        await SeedAsync(stateManager);
        await SeedPlayerAsync(stateManager, "second-player");

        var started = 0;
        var maxConcurrent = 0;
        var gate = new object();

        var narrator = new Mock<INarratorService>();
        narrator.Setup(s => s.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (gate) { started++; maxConcurrent = Math.Max(maxConcurrent, started); }
                await Task.Delay(60);
                lock (gate) { started--; }
                return new FreeFormResponse
                {
                    Success = true,
                    Narration = "\"Mara answers you.\"",
                    InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Conversation, NpcDisposition = "neutral" }
                };
            });

        var engine = CreateEngine(stateManager, narrator.Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "talk to mara"));
        await engine.ProcessActionAsync("second-player", engine.ParseCommand("second-player", "talk to mara"));

        await Task.WhenAll(
            engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "tell me one")),
            engine.ProcessActionAsync("second-player", engine.ParseCommand("second-player", "tell me two")));

        // Per-player locking must not serialize the whole server.
        Assert.Equal(2, maxConcurrent);
    }

    private static async Task SeedAsync(InMemoryStateManager stateManager)
    {
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "The Rusted Flagon",
            Description = "A creaky tavern.",
            Npcs = [new Npc { Id = "mara", Name = "Mara", Personality = "Barkeep", Disposition = "neutral" }],
            Exits = new Dictionary<string, string> { ["east"] = "square" }
        });
        await SeedPlayerAsync(stateManager, PlayerId);
    }

    private static Task SeedPlayerAsync(InMemoryStateManager stateManager, string playerId)
        => stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = playerId, Name = playerId, Race = "Human", Class = "Warrior", Level = 3,
            CurrentRoomId = "tavern", Hp = 20, MaxHp = 20, Mp = 8, MaxMp = 8,
            Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10
        });

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string e, string p) => new DiceRoll { Expression = e, Purpose = p, IndividualRolls = [10], Total = 10 });

        return new GameEngine(
            stateManager,
            dice.Object,
            narrator,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }
}
