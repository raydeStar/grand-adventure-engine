using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.BlindAdventure;

/// <summary>
/// Manages the lifecycle of a Blind Adventure session: start, track progress, end.
/// Orchestrates the room generator, narrator conclusion, and player state transitions.
/// </summary>
public class BlindAdventureService
{
    private readonly IBlindAdventureRoomGenerator _roomGenerator;
    private readonly INarratorService _narrator;
    private readonly IStateManager _stateManager;
    private readonly ILogger<BlindAdventureService> _logger;

    public BlindAdventureService(
        IBlindAdventureRoomGenerator roomGenerator,
        INarratorService narrator,
        IStateManager stateManager,
        ILogger<BlindAdventureService> logger)
    {
        _roomGenerator = roomGenerator;
        _narrator = narrator;
        _stateManager = stateManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts a new Blind Adventure session for the given player.
    /// Generates the starting room, sets the player's interaction mode, and saves state.
    /// </summary>
    public async Task<(Room StartingRoom, string Narration)> StartAdventureAsync(
        PlayerCharacter player, StorylineContext storyline, CancellationToken ct = default)
    {
        if (player.BlindAdventure is not null)
            throw new InvalidOperationException("Player is already in a blind adventure.");

        var session = new BlindAdventureSession
        {
            Storyline = storyline,
            PreviousRoomId = player.CurrentRoomId,
            StartedAt = DateTimeOffset.UtcNow
        };

        // Generate the starting room
        var startingRoomId = $"blind_{storyline.Id}_start";
        var startingRoom = new Room
        {
            Id = startingRoomId,
            Name = storyline.Name,
            Description = storyline.StartingRoomDescription,
            EnvironmentTags = ["blind_adventure"],
            Npcs = [],
            Items = [],
            Exits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        // Give the starting room a forward exit so the player can move
        startingRoom.Exits["north"] = $"blind_{startingRoomId}_north";

        startingRoom.IsDiscovered = true;
        startingRoom.DiscoveredAt = DateTimeOffset.UtcNow;
        startingRoom.WorldIds = [player.ActiveWorldId];
        await _stateManager.SaveRoomAsync(startingRoom, ct);

        session.RoomsGenerated = 1;
        session.VisitedRoomIds.Add(startingRoomId);
        session.VisitedRoomSummaries.Add($"{startingRoom.Name}: {startingRoom.Description}");

        // Update player state
        player.BlindAdventure = session;
        player.CurrentRoomId = startingRoomId;
        player.Interaction.Mode = InteractionMode.BlindAdventure;
        player.Interaction.Target = storyline.Id;
        await _stateManager.SavePlayerAsync(player, ct);

        _logger.LogInformation(
            "Blind adventure started for {PlayerId}: storyline={StorylineId}, maxRooms={MaxRooms}",
            player.Id, storyline.Id, storyline.MaxRooms);

        return (startingRoom, startingRoom.Description);
    }

    /// <summary>
    /// Records that the player entered a new room during the adventure.
    /// Increments room count and tracks the summary for narrator context.
    /// </summary>
    public void TrackRoomVisit(PlayerCharacter player, Room room)
    {
        var session = player.BlindAdventure;
        if (session is null) return;

        if (!session.VisitedRoomIds.Contains(room.Id))
        {
            session.VisitedRoomIds.Add(room.Id);
            session.VisitedRoomSummaries.Add($"{room.Name}: {room.Description}");
            session.RoomsGenerated++;
        }
    }

    /// <summary>
    /// Records a notable event during the adventure (for the conclusion summary).
    /// </summary>
    public void TrackKeyEvent(PlayerCharacter player, string eventDescription)
    {
        player.BlindAdventure?.KeyEvents.Add(eventDescription);
    }

    /// <summary>
    /// Returns true if the adventure has reached its room cap and should conclude.
    /// </summary>
    public static bool ShouldConclude(PlayerCharacter player)
        => player.BlindAdventure is not null && player.BlindAdventure.RoomsRemaining <= 0;

    /// <summary>
    /// Ends the current Blind Adventure session.
    /// Calls the narrator for a conclusion, restores previous room, and clears session state.
    /// </summary>
    public async Task<(string Narration, string Summary, BlindAdventureSession FinalSession)> EndAdventureAsync(
        PlayerCharacter player, CancellationToken ct = default)
    {
        var session = player.BlindAdventure
            ?? throw new InvalidOperationException("Player is not in a blind adventure.");

        // Get the conclusion narration from the narrator
        string narration;
        string summary;
        try
        {
            (narration, summary) = await _narrator.NarrateBlindAdventureConclusionAsync(
                session.Storyline,
                session.VisitedRoomSummaries,
                session.KeyEvents,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to narrate blind adventure conclusion for {PlayerId}", player.Id);
            narration = "The adventure ends — and with it, a chapter of your story closes.";
            summary = $"Explored {session.VisitedRoomIds.Count} rooms in {session.Storyline.Name}.";
        }

        var finalSession = session;

        // Restore player to their previous state
        player.CurrentRoomId = session.PreviousRoomId;
        player.BlindAdventure = null;
        player.Interaction.Reset();
        await _stateManager.SavePlayerAsync(player, ct);

        _logger.LogInformation(
            "Blind adventure ended for {PlayerId}: rooms={Rooms}, events={Events}",
            player.Id, finalSession.VisitedRoomIds.Count, finalSession.KeyEvents.Count);

        return (narration, summary, finalSession);
    }
}
