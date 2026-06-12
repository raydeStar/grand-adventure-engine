using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.BlindAdventure;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class GameEngineOnboardingTests
{
    [Fact]
    public async Task CreateCharacterFromConcept_UsesRequestedWorldSpawnAndPersistsWorldState()
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "moon-gate",
            Name = "Moon Gate",
            Description = "A silver threshold beneath a pale sky.",
            WorldIds = ["moon-realm"]
        });

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        var worldRepository = new InMemoryWorldRepository();
        await worldRepository.SaveWorldAsync(new World
        {
            Id = "moon-realm",
            Name = "Moon Realm",
            SpawnRoomId = "moon-gate",
            DefaultNarratorPresetId = "lunar-voice"
        });

        var engine = new GameEngine(
            stateManager,
            dice.Object,
            narrator.Object,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance,
            worldRepository: worldRepository);

        var player = await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = "world-create-1",
            Name = "Selene",
            Race = "Human",
            Class = "Cleric",
            Backstory = "A moonlit pilgrim.",
            WorldId = "moon-realm"
        });

        Assert.Equal("moon-realm", player.ActiveWorldId);
        Assert.Equal("moon-realm", player.HomeWorldId);
        Assert.Equal("moon-gate", player.CurrentRoomId);
        Assert.Equal("lunar-voice", player.NarratorPresetId);

        var worldState = await worldRepository.GetPlayerWorldStateAsync(player.Id, "moon-realm");
        Assert.NotNull(worldState);
        Assert.Equal("moon-gate", worldState!.CurrentRoomId);
        Assert.True(worldState.HasVisited);
    }

    [Fact]
    public async Task ProcessActionAsync_BlindMove_UsesBlindAdventureGenerator()
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = "blind-player",
            Name = "Rook",
            Race = "Human",
            Class = "Rogue",
            CurrentRoomId = "spawn",
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10
        });

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Blind move narration.");

        var roomGenerator = new Mock<IBlindAdventureRoomGenerator>();
        roomGenerator
            .Setup(generator => generator.GenerateAndPersistRoomAsync(
                "blind-player",
                It.IsAny<Room>(),
                "north",
                It.IsAny<StorylineContext>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Room
            {
                Id = "blind_blind_haunted-manor_start_north",
                Name = "Blind Hall",
                Description = "A generated hallway for the Blind Adventure.",
                WorldIds = [WorldDefaults.DefaultWorldId],
                Exits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["south"] = "blind_haunted-manor_start"
                }
            });

        var blindService = new BlindAdventureService(
            roomGenerator.Object,
            narrator.Object,
            stateManager,
            NullLogger<BlindAdventureService>.Instance);

        var player = await stateManager.GetPlayerAsync("blind-player");
        await blindService.StartAdventureAsync(player!, new StorylineContext
        {
            Id = "haunted-manor",
            Name = "Haunted Manor",
            StartingRoomDescription = "A dusty foyer.",
            MaxRooms = 3
        });

        var engine = new GameEngine(
            stateManager,
            new Mock<IProbabilityEngine>(MockBehavior.Strict).Object,
            narrator.Object,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance,
            blindAdventureService: blindService);

        var action = engine.ParseCommand("blind-player", "go north");
        var result = await engine.ProcessActionAsync("blind-player", action);

        Assert.True(result.Success);
        Assert.Equal("blind_blind_haunted-manor_start_north", result.NewRoom?.Id);

        var updatedPlayer = await stateManager.GetPlayerAsync("blind-player");
        Assert.NotNull(updatedPlayer?.BlindAdventure);
        Assert.Equal("blind_blind_haunted-manor_start_north", updatedPlayer!.CurrentRoomId);
        Assert.Equal(2, updatedPlayer.BlindAdventure!.RoomsGenerated);

        roomGenerator.Verify(generator => generator.GenerateAndPersistRoomAsync(
            "blind-player",
            It.IsAny<Room>(),
            "north",
            It.IsAny<StorylineContext>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
