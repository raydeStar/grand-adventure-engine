namespace GAE.Core.Models;

/// <summary>
/// Shared constants for the engine's implicit single-world baseline.
/// Existing content and save data default to this world until a multi-world
/// migration explicitly places them elsewhere.
/// </summary>
public static class WorldDefaults
{
    public const string DefaultWorldId = "default-world";
    public const string DefaultWorldName = "Primary World";
    public const string DefaultSpawnRoomId = "spawn";
}