using GAE.Core.Models;

namespace GAE.Core.Interfaces;

/// <summary>
/// Generates rooms on-demand for Blind Adventure sessions using the storyline context,
/// visited room history, and AI narrator to produce coherent, narrative-driven rooms.
/// </summary>
public interface IBlindAdventureRoomGenerator
{
    /// <summary>
    /// Generates a new room when the player moves in a direction with no predefined room.
    /// The room is wired bidirectionally, persisted, and returned ready for the player to enter.
    /// </summary>
    Task<Room> GenerateAndPersistRoomAsync(
        string playerId,
        Room currentRoom,
        string direction,
        StorylineContext storyline,
        IReadOnlyList<string> visitedRoomSummaries,
        string? nextPlotBeat,
        int roomsRemaining,
        CancellationToken ct = default);
}
