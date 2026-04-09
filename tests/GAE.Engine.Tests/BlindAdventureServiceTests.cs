using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.BlindAdventure;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class BlindAdventureServiceTests
{
    private static StorylineContext CreateTestStoryline() => new()
    {
        Id = "haunted-manor",
        Name = "The Haunted Manor",
        Setting = "A crumbling gothic estate on a windswept moor",
        Tone = "ominous, darkly wry",
        Theme = "isolation, inherited guilt",
        PlotBeats = ["Discover the sealed east wing", "Find the portrait gallery"],
        StartingRoomDescription = "The manor looms ahead, its silhouette jagged against a sullen sky.",
        MaxRooms = 5
    };

    private static PlayerCharacter CreateTestPlayer() => new()
    {
        Id = "player-1",
        Name = "Aldric",
        CurrentRoomId = "spawn",
        Hp = 20,
        MaxHp = 20,
        ActiveWorldId = "default-world"
    };

    private (BlindAdventureService Service, InMemoryStateManager State, Mock<INarratorService> Narrator) CreateService()
    {
        var state = new InMemoryStateManager();
        var narrator = new Mock<INarratorService>();
        var roomGen = new Mock<IBlindAdventureRoomGenerator>();
        var service = new BlindAdventureService(
            roomGen.Object,
            narrator.Object,
            state,
            NullLogger<BlindAdventureService>.Instance);
        return (service, state, narrator);
    }

    [Fact]
    public async Task StartAdventure_CreatesSessionAndMovesPlayer()
    {
        var (service, state, _) = CreateService();
        var player = CreateTestPlayer();
        await state.SavePlayerAsync(player);
        var storyline = CreateTestStoryline();

        var (startingRoom, narration) = await service.StartAdventureAsync(player, storyline);

        Assert.NotNull(player.BlindAdventure);
        Assert.Equal("haunted-manor", player.BlindAdventure.Storyline.Id);
        Assert.Equal("spawn", player.BlindAdventure.PreviousRoomId);
        Assert.Equal(startingRoom.Id, player.CurrentRoomId);
        Assert.Equal(InteractionMode.BlindAdventure, player.Interaction.Mode);
        Assert.Equal(1, player.BlindAdventure.RoomsGenerated);
        Assert.Single(player.BlindAdventure.VisitedRoomIds);
        Assert.Contains(narration, storyline.StartingRoomDescription);
    }

    [Fact]
    public async Task StartAdventure_GeneratesStartingRoomWithExit()
    {
        var (service, state, _) = CreateService();
        var player = CreateTestPlayer();
        await state.SavePlayerAsync(player);
        var storyline = CreateTestStoryline();

        var (startingRoom, _) = await service.StartAdventureAsync(player, storyline);

        Assert.NotNull(startingRoom);
        Assert.True(startingRoom.IsDiscovered);
        Assert.NotEmpty(startingRoom.Exits);
        Assert.Contains("blind_adventure", startingRoom.EnvironmentTags);

        // Room should be persisted
        var persisted = await state.GetPlayerRoomAsync(player.Id, startingRoom.Id);
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task StartAdventure_ThrowsIfAlreadyInAdventure()
    {
        var (service, state, _) = CreateService();
        var player = CreateTestPlayer();
        player.BlindAdventure = new BlindAdventureSession { Storyline = CreateTestStoryline() };
        await state.SavePlayerAsync(player);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAdventureAsync(player, CreateTestStoryline()));
    }

    [Fact]
    public void TrackRoomVisit_IncrementsCountAndRecordsSummary()
    {
        var player = CreateTestPlayer();
        player.BlindAdventure = new BlindAdventureSession { Storyline = CreateTestStoryline() };

        var room = new Room { Id = "room-2", Name = "Dark Corridor", Description = "A narrow hallway." };
        var (service, _, _) = CreateService();
        service.TrackRoomVisit(player, room);

        Assert.Contains("room-2", player.BlindAdventure.VisitedRoomIds);
        Assert.Single(player.BlindAdventure.VisitedRoomSummaries);
        Assert.Equal(1, player.BlindAdventure.RoomsGenerated);
    }

    [Fact]
    public void TrackRoomVisit_DoesNotDoubleCountRevisits()
    {
        var player = CreateTestPlayer();
        player.BlindAdventure = new BlindAdventureSession { Storyline = CreateTestStoryline() };

        var room = new Room { Id = "room-2", Name = "Dark Corridor", Description = "A narrow hallway." };
        var (service, _, _) = CreateService();
        service.TrackRoomVisit(player, room);
        service.TrackRoomVisit(player, room); // revisit

        Assert.Single(player.BlindAdventure.VisitedRoomIds);
        Assert.Equal(1, player.BlindAdventure.RoomsGenerated);
    }

    [Fact]
    public void TrackKeyEvent_AddsToSessionEvents()
    {
        var player = CreateTestPlayer();
        player.BlindAdventure = new BlindAdventureSession { Storyline = CreateTestStoryline() };

        var (service, _, _) = CreateService();
        service.TrackKeyEvent(player, "Defeated the cellar spider");

        Assert.Single(player.BlindAdventure.KeyEvents);
        Assert.Equal("Defeated the cellar spider", player.BlindAdventure.KeyEvents[0]);
    }

    [Fact]
    public void ShouldConclude_TrueWhenRoomCapReached()
    {
        var player = CreateTestPlayer();
        var storyline = CreateTestStoryline(); // MaxRooms = 5
        player.BlindAdventure = new BlindAdventureSession
        {
            Storyline = storyline,
            RoomsGenerated = 5
        };

        Assert.True(BlindAdventureService.ShouldConclude(player));
    }

    [Fact]
    public void ShouldConclude_FalseWhenRoomsRemain()
    {
        var player = CreateTestPlayer();
        player.BlindAdventure = new BlindAdventureSession
        {
            Storyline = CreateTestStoryline(),
            RoomsGenerated = 3
        };

        Assert.False(BlindAdventureService.ShouldConclude(player));
    }

    [Fact]
    public void ShouldConclude_FalseWhenNoSession()
    {
        var player = CreateTestPlayer();
        Assert.False(BlindAdventureService.ShouldConclude(player));
    }

    [Fact]
    public async Task EndAdventure_RestoresPreviousRoomAndClearsSession()
    {
        var (service, state, narrator) = CreateService();
        var player = CreateTestPlayer();
        var storyline = CreateTestStoryline();
        await state.SavePlayerAsync(player);

        // Start the adventure
        await service.StartAdventureAsync(player, storyline);
        Assert.NotEqual("spawn", player.CurrentRoomId);

        // Setup narrator conclusion mock
        narrator.Setup(n => n.NarrateBlindAdventureConclusionAsync(
            It.IsAny<StorylineContext>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(("The manor releases its grip on you.", "Explored the manor and survived."));

        // End the adventure
        var (narration, summary, finalSession) = await service.EndAdventureAsync(player);

        Assert.Equal("spawn", player.CurrentRoomId);
        Assert.Null(player.BlindAdventure);
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
        Assert.Equal("The manor releases its grip on you.", narration);
        Assert.Contains("survived", summary);
        Assert.Equal("haunted-manor", finalSession.Storyline.Id);
    }

    [Fact]
    public async Task EndAdventure_ThrowsIfNotInAdventure()
    {
        var (service, state, _) = CreateService();
        var player = CreateTestPlayer();
        await state.SavePlayerAsync(player);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EndAdventureAsync(player));
    }

    [Fact]
    public async Task EndAdventure_UsesFallbackWhenNarratorFails()
    {
        var (service, state, narrator) = CreateService();
        var player = CreateTestPlayer();
        var storyline = CreateTestStoryline();
        await state.SavePlayerAsync(player);

        await service.StartAdventureAsync(player, storyline);

        narrator.Setup(n => n.NarrateBlindAdventureConclusionAsync(
            It.IsAny<StorylineContext>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("LM Studio offline"));

        var (narration, summary, _) = await service.EndAdventureAsync(player);

        Assert.NotNull(narration);
        Assert.NotNull(summary);
        Assert.Null(player.BlindAdventure);
        Assert.Equal("spawn", player.CurrentRoomId);
    }

    [Fact]
    public void SessionModel_RoomsRemainingCalculation()
    {
        var session = new BlindAdventureSession
        {
            Storyline = new StorylineContext { MaxRooms = 10 },
            RoomsGenerated = 7
        };

        Assert.Equal(3, session.RoomsRemaining);
    }

    [Fact]
    public void SessionModel_NextPlotBeatTracking()
    {
        var session = new BlindAdventureSession
        {
            Storyline = new StorylineContext
            {
                PlotBeats = ["Beat 1", "Beat 2", "Beat 3"]
            },
            PlotBeatsDelivered = 0
        };

        Assert.Equal("Beat 1", session.NextPlotBeat);

        session.PlotBeatsDelivered = 2;
        Assert.Equal("Beat 3", session.NextPlotBeat);

        session.PlotBeatsDelivered = 3;
        Assert.Null(session.NextPlotBeat);
    }
}
