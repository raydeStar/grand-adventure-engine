using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.BlindAdventure;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class BlindAdventureRoomGeneratorTests
{
    private static StorylineContext CreateTestStoryline() => new()
    {
        Id = "test-storyline",
        Name = "The Haunted Manor",
        Setting = "A crumbling gothic estate on a windswept moor",
        Tone = "ominous, darkly wry",
        Theme = "isolation, inherited guilt",
        PlotBeats = ["Discover the sealed east wing", "Find the portrait gallery"],
        StartingRoomDescription = "The manor looms ahead.",
        MaxRooms = 10
    };

    private static Room CreateSourceRoom() => new()
    {
        Id = "manor_foyer",
        Name = "The Grand Foyer",
        Description = "Dust motes drift through shafts of grey light.",
        EnvironmentTags = ["indoor", "gothic", "blind_adventure"],
        WorldIds = ["default-world"],
        Exits = new Dictionary<string, string> { ["south"] = "manor_entrance" }
    };

    [Fact]
    public async Task GenerateAndPersist_CallsNarratorAndPersistsRoom()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var storyline = CreateTestStoryline();
        var source = CreateSourceRoom();

        // Seed source room and exit reference so generator can resolve target ID
        source.Exits["north"] = "blind_manor_foyer_north";
        await state.SaveRoomAsync(source);

        var generatedRoom = new Room
        {
            Id = "blind_manor_foyer_north",
            Name = "The Dusty Corridor",
            Description = "A corridor lined with faded portraits.",
            EnvironmentTags = ["blind_adventure"],
            Exits = new Dictionary<string, string> { ["south"] = "manor_foyer", ["east"] = "blind_blind_manor_foyer_north_east" },
            Npcs = [],
            Items = []
        };

        narrator.Setup(n => n.GenerateBlindAdventureRoomAsync(
                "blind_manor_foyer_north", "north", It.IsAny<Room>(),
                storyline, It.IsAny<IReadOnlyList<string>>(),
                "Discover the sealed east wing", 8, It.IsAny<CancellationToken>()))
            .ReturnsAsync(generatedRoom);

        var generator = new BlindAdventureRoomGenerator(
            narrator.Object, state, NullLogger<BlindAdventureRoomGenerator>.Instance);

        var result = await generator.GenerateAndPersistRoomAsync(
            "player1", source, "north", storyline,
            ["The Grand Foyer — dust motes drift through grey light"],
            "Discover the sealed east wing", 8);

        Assert.Equal("blind_manor_foyer_north", result.Id);
        Assert.Equal("The Dusty Corridor", result.Name);
        Assert.True(result.IsDiscovered);
        Assert.NotNull(result.DiscoveredAt);
        Assert.Contains("default-world", result.WorldIds);

        // Verify reverse exit is wired
        Assert.Equal("manor_foyer", result.Exits["south"]);

        // Verify room was persisted
        var persisted = await state.GetPlayerRoomAsync("player1", "blind_manor_foyer_north");
        Assert.NotNull(persisted);
        Assert.Equal("The Dusty Corridor", persisted.Name);
    }

    [Fact]
    public async Task GenerateAndPersist_ReturnsExistingRoomOnRevisit()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var storyline = CreateTestStoryline();
        var source = CreateSourceRoom();
        source.Exits["north"] = "blind_manor_foyer_north";

        // Pre-persist a room at the target ID
        var existing = new Room
        {
            Id = "blind_manor_foyer_north",
            Name = "Previously Visited Room",
            Description = "You remember this place.",
            IsDiscovered = true,
            WorldIds = ["default-world"],
            Exits = new Dictionary<string, string> { ["south"] = "manor_foyer" }
        };
        await state.SaveRoomAsync(existing);

        var generator = new BlindAdventureRoomGenerator(
            narrator.Object, state, NullLogger<BlindAdventureRoomGenerator>.Instance);

        var result = await generator.GenerateAndPersistRoomAsync(
            "player1", source, "north", storyline,
            [], null, 5);

        // Should return cached room, NOT call narrator
        Assert.Equal("Previously Visited Room", result.Name);
        narrator.Verify(
            n => n.GenerateBlindAdventureRoomAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Room>(),
                It.IsAny<StorylineContext>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAndPersist_UsesFallbackWhenNarratorThrows()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var storyline = CreateTestStoryline();
        var source = CreateSourceRoom();
        source.Exits["north"] = "blind_manor_foyer_north";

        narrator.Setup(n => n.GenerateBlindAdventureRoomAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Room>(),
                It.IsAny<StorylineContext>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("LM Studio down"));

        var generator = new BlindAdventureRoomGenerator(
            narrator.Object, state, NullLogger<BlindAdventureRoomGenerator>.Instance);

        var result = await generator.GenerateAndPersistRoomAsync(
            "player1", source, "north", storyline,
            [], null, 5);

        // Should return a fallback room, not throw
        Assert.Equal("blind_manor_foyer_north", result.Id);
        Assert.True(result.IsDiscovered);
        Assert.Contains("south", result.Exits.Keys);
        Assert.Equal("manor_foyer", result.Exits["south"]);

        // Verify it was persisted even as fallback
        var persisted = await state.GetPlayerRoomAsync("player1", "blind_manor_foyer_north");
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task GenerateAndPersist_WiresForwardExitOnSourceRoom()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var storyline = CreateTestStoryline();
        var source = CreateSourceRoom();
        // Source does NOT have a "north" exit yet — generator should add it
        await state.SaveRoomAsync(source);

        narrator.Setup(n => n.GenerateBlindAdventureRoomAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Room>(),
                It.IsAny<StorylineContext>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Room
            {
                Id = "blind_manor_foyer_north",
                Name = "New Chamber",
                Description = "Something lurks here.",
                EnvironmentTags = ["blind_adventure"],
                Exits = new Dictionary<string, string> { ["south"] = "manor_foyer" }
            });

        var generator = new BlindAdventureRoomGenerator(
            narrator.Object, state, NullLogger<BlindAdventureRoomGenerator>.Instance);

        await generator.GenerateAndPersistRoomAsync(
            "player1", source, "north", storyline,
            [], null, 7);

        // Source room should now have the forward exit wired
        Assert.True(source.Exits.ContainsKey("north"));
        Assert.Equal("blind_manor_foyer_north", source.Exits["north"]);
    }
}
