using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.BlindAdventure;

/// <summary>
/// Generates and persists rooms on-demand for Blind Adventure sessions.
/// Orchestrates the narrator call, exit wiring, and state persistence so
/// GameEngine can delegate blind-mode room creation without embedding
/// storyline concerns in the movement loop.
/// </summary>
public class BlindAdventureRoomGenerator : IBlindAdventureRoomGenerator
{
    private readonly INarratorService _narrator;
    private readonly IStateManager _stateManager;
    private readonly ILogger<BlindAdventureRoomGenerator> _logger;

    public BlindAdventureRoomGenerator(
        INarratorService narrator,
        IStateManager stateManager,
        ILogger<BlindAdventureRoomGenerator> logger)
    {
        _narrator = narrator;
        _stateManager = stateManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Room> GenerateAndPersistRoomAsync(
        string playerId,
        Room currentRoom,
        string direction,
        StorylineContext storyline,
        IReadOnlyList<string> visitedRoomSummaries,
        string? nextPlotBeat,
        int roomsRemaining,
        CancellationToken ct = default)
    {
        var sourceRoomId = StripPlayerPrefix(currentRoom.Id);
        var targetRoomId = currentRoom.Exits.TryGetValue(direction, out var existingTarget)
            ? StripPlayerPrefix(existingTarget)
            : $"blind_{sourceRoomId}_{direction}";

        // Check if room already exists (revisit case)
        var existing = await _stateManager.GetPlayerRoomAsync(playerId, targetRoomId, ct);
        if (existing is not null)
        {
            _logger.LogDebug("Blind adventure room {RoomId} already exists, returning cached", targetRoomId);
            return existing;
        }

        // Build a clean source room for the narrator (no player-prefixed IDs)
        var sourceForGen = new Room
        {
            Id = sourceRoomId,
            Name = currentRoom.Name,
            Description = currentRoom.Description,
            EnvironmentTags = currentRoom.EnvironmentTags,
            Exits = currentRoom.Exits
        };

        Room generated;
        try
        {
            generated = await _narrator.GenerateBlindAdventureRoomAsync(
                targetRoomId, direction, sourceForGen,
                storyline, visitedRoomSummaries, nextPlotBeat, roomsRemaining, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blind adventure narrator call failed for {RoomId}", targetRoomId);
            generated = BuildMinimalFallback(targetRoomId, direction, sourceRoomId);
        }

        // Ensure core fields are set correctly
        generated.Id = targetRoomId;
        generated.IsDiscovered = true;
        generated.DiscoveredAt = DateTimeOffset.UtcNow;
        generated.WorldIds = currentRoom.WorldIds?.ToList() ?? [WorldDefaults.DefaultWorldId];

        // Ensure reverse exit back to source
        generated.Exits[OppositeDirection(direction)] = sourceRoomId;

        // Wire forward exit on the source room if it doesn't already point here
        if (!currentRoom.Exits.ContainsKey(direction))
        {
            currentRoom.Exits[direction] = targetRoomId;
            await _stateManager.SaveRoomAsync(currentRoom, ct);
        }

        await _stateManager.SaveRoomAsync(generated, ct);

        _logger.LogInformation(
            "Blind adventure generated room {RoomId} ({RoomName}) {Direction} of {SourceRoom}",
            targetRoomId, generated.Name, direction, sourceRoomId);

        return generated;
    }

    private static Room BuildMinimalFallback(string roomId, string direction, string sourceRoomId) => new()
    {
        Id = roomId,
        Name = "The Next Uncertain Chamber",
        Description = "A dimly lit area stretches before you, pregnant with unresolved possibility.",
        EnvironmentTags = ["blind_adventure"],
        Npcs = [],
        Items = [],
        Exits = new Dictionary<string, string>
        {
            [OppositeDirection(direction)] = sourceRoomId
        }
    };

    private static string StripPlayerPrefix(string roomId)
    {
        var colonIdx = roomId.LastIndexOf(':');
        return colonIdx >= 0 ? roomId[(colonIdx + 1)..] : roomId;
    }

    private static string OppositeDirection(string dir) => dir switch
    {
        "north" => "south", "south" => "north",
        "east" => "west", "west" => "east",
        "up" => "down", "down" => "up",
        _ => "back"
    };
}
