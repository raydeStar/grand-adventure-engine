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
            Exits = new Dictionary<string, string>
            {
                ["south"] = "manor_foyer",
                ["east"] = "blind_blind_manor_foyer_north_east"
            },
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

        // Verify forward exit kept
        Assert.True(result.Exits.ContainsKey("east"));

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

    // ─── F03: Exit validation and connectivity tests ───────────────────

    [Fact]
    public void SanitizeExits_AlwaysIncludesBackLink()
    {
        var raw = new Dictionary<string, string>
        {
            ["east"] = "room_east"
        };

        var result = BlindAdventureRoomGenerator.SanitizeExits(
            raw, "north", "source_room", "target_room", roomsRemaining: 5);

        Assert.Equal("source_room", result["south"]);
    }

    [Fact]
    public void SanitizeExits_StripsInvalidDirections()
    {
        var raw = new Dictionary<string, string>
        {
            ["north"] = "room_north",
            ["gibberish"] = "room_invalid",
            ["42"] = "room_number",
            ["east"] = "room_east"
        };

        var result = BlindAdventureRoomGenerator.SanitizeExits(
            raw, "south", "source_room", "target_room", roomsRemaining: 5);

        Assert.True(result.ContainsKey("north")); // back-link
        Assert.True(result.ContainsKey("east"));   // valid forward
        Assert.False(result.ContainsKey("gibberish"));
        Assert.False(result.ContainsKey("42"));
        // north (back-link) + east (forward)
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SanitizeExits_PreventsSelfReferencingExits()
    {
        var raw = new Dictionary<string, string>
        {
            ["east"] = "target_room", // points back to self
            ["west"] = "room_west"
        };

        var result = BlindAdventureRoomGenerator.SanitizeExits(
            raw, "south", "source_room", "target_room", roomsRemaining: 5);

        Assert.False(result.ContainsKey("east")); // self-ref removed
        Assert.True(result.ContainsKey("west"));
    }

    [Fact]
    public void SanitizeExits_DeadEndWhenRoomCapExhausted()
    {
        var raw = new Dictionary<string, string>
        {
            ["north"] = "room_ahead",
            ["east"] = "room_side",
            ["south"] = "source_room" // back-link
        };

        var result = BlindAdventureRoomGenerator.SanitizeExits(
            raw, "north", "source_room", "target_room", roomsRemaining: 0);

        // Only the back-link should remain
        Assert.Single(result);
        Assert.Equal("source_room", result["south"]);
    }

    [Fact]
    public void SanitizeExits_AssignsPendingIdsToEmptyTargets()
    {
        var raw = new Dictionary<string, string>
        {
            ["east"] = "",   // narrator didn't provide an ID
            ["west"] = "  " // whitespace only
        };

        var result = BlindAdventureRoomGenerator.SanitizeExits(
            raw, "south", "source_room", "target_room", roomsRemaining: 5);

        Assert.Equal("blind_target_room_east", result["east"]);
        Assert.Equal("blind_target_room_west", result["west"]);
    }

    [Fact]
    public async Task GenerateAndPersist_MaxRoomCapCreatesFinalRoom()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var storyline = CreateTestStoryline();
        var source = CreateSourceRoom();
        source.Exits["north"] = "blind_manor_foyer_north";

        // Narrator returns a room with extra forward exits
        narrator.Setup(n => n.GenerateBlindAdventureRoomAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Room>(),
                It.IsAny<StorylineContext>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Room
            {
                Id = "blind_manor_foyer_north",
                Name = "The Final Chamber",
                Description = "The story reaches its end here.",
                EnvironmentTags = ["blind_adventure"],
                Exits = new Dictionary<string, string>
                {
                    ["south"] = "manor_foyer",
                    ["east"] = "room_beyond",
                    ["north"] = "room_further"
                }
            });

        var generator = new BlindAdventureRoomGenerator(
            narrator.Object, state, NullLogger<BlindAdventureRoomGenerator>.Instance);

        // roomsRemaining = 0 → should strip forward exits
        var result = await generator.GenerateAndPersistRoomAsync(
            "player1", source, "north", storyline,
            [], null, roomsRemaining: 0);

        // Only the back-link should remain
        Assert.Single(result.Exits);
        Assert.Equal("manor_foyer", result.Exits["south"]);
    }

    [Fact]
    public async Task GenerateAndPersist_ForwardExitLeadsToNewGenerationOnTraversal()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var storyline = CreateTestStoryline();
        var source = CreateSourceRoom();
        source.Exits["north"] = "blind_manor_foyer_north";
        await state.SaveRoomAsync(source);

        // First generation: room with forward exit "east"
        var firstRoom = new Room
        {
            Id = "blind_manor_foyer_north",
            Name = "The Dusty Corridor",
            Description = "A corridor with a passage east.",
            EnvironmentTags = ["blind_adventure"],
            Exits = new Dictionary<string, string>
            {
                ["south"] = "manor_foyer",
                ["east"] = "blind_blind_manor_foyer_north_east"
            }
        };

        narrator.Setup(n => n.GenerateBlindAdventureRoomAsync(
                "blind_manor_foyer_north", "north", It.IsAny<Room>(),
                It.IsAny<StorylineContext>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstRoom);

        // Second generation: room east of corridor
        var secondRoom = new Room
        {
            Id = "blind_blind_manor_foyer_north_east",
            Name = "The Portrait Gallery",
            Description = "Dozens of eyes follow your every move.",
            EnvironmentTags = ["blind_adventure"],
            Exits = new Dictionary<string, string>
            {
                ["west"] = "blind_manor_foyer_north"
            }
        };

        narrator.Setup(n => n.GenerateBlindAdventureRoomAsync(
                "blind_blind_manor_foyer_north_east", "east", It.IsAny<Room>(),
                It.IsAny<StorylineContext>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondRoom);

        var generator = new BlindAdventureRoomGenerator(
            narrator.Object, state, NullLogger<BlindAdventureRoomGenerator>.Instance);

        // Step 1: move north
        var corridor = await generator.GenerateAndPersistRoomAsync(
            "player1", source, "north", storyline,
            [], "Discover the sealed east wing", 8);

        // Verify corridor has forward "east" exit
        Assert.True(corridor.Exits.ContainsKey("east"));

        // Step 2: move east from corridor (traversing the forward exit)
        var gallery = await generator.GenerateAndPersistRoomAsync(
            "player1", corridor, "east", storyline,
            ["The Dusty Corridor"], "Find the portrait gallery", 7);

        Assert.Equal("The Portrait Gallery", gallery.Name);
        Assert.Equal("blind_manor_foyer_north", gallery.Exits["west"]);
    }
}
