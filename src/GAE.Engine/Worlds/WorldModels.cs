using GAE.Core.Models;
using GAE.Engine.Configuration;

namespace GAE.Engine.Worlds;

/// <summary>
/// Top-level world definition for the multi-world system.
/// Each world carries its own ruleset, spawn room, tags, and portal metadata.
/// </summary>
public class World
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SpawnRoomId { get; set; } = WorldDefaults.DefaultSpawnRoomId;
    public bool IsActive { get; set; } = true;
    public GameRulesConfig Rules { get; set; } = new();
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Tags { get; set; } = [];
    public List<WorldPortal> Portals { get; set; } = [];

    /// <summary>Narrator preset IDs available for selection in this world.</summary>
    public List<string> NarratorPresetIds { get; set; } = [];

    /// <summary>Default narrator preset assigned to new characters in this world. Null = system default.</summary>
    public string? DefaultNarratorPresetId { get; set; }

    /// <summary>
    /// Discord intro message shown when a player starts character creation in this world.
    /// Supports Discord markdown. If null/empty, a generic intro is used.
    /// </summary>
    public string? CharacterCreationIntro { get; set; }
}

/// <summary>
/// Connects one world to another through an authored portal or admin-defined route.
/// </summary>
public class WorldPortal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceWorldId { get; set; } = string.Empty;
    public string SourceRoomId { get; set; } = string.Empty;
    public string DestinationWorldId { get; set; } = string.Empty;
    public string? DestinationRoomId { get; set; }
    public string? Description { get; set; }
    public string? NarratorHint { get; set; }
    public bool IsAdminOnly { get; set; }
    public int? MinLevel { get; set; }
    public List<string> RequiredCompletedQuests { get; set; } = [];
}

/// <summary>
/// Frozen stat snapshot for restoring a player's native state when they return to a world.
/// </summary>
public class WorldStatSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public Dictionary<string, int> Stats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Class { get; set; }
    public string? Race { get; set; }
    public int Level { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Mp { get; set; }
    public int MaxMp { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Stores the narrator-approved stat translation between two worlds for a specific player.
/// Includes source-level and source-stats so the cache can be invalidated when the player changes.
/// </summary>
public class StatTranslationHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlayerId { get; set; } = string.Empty;
    public string SourceWorldId { get; set; } = string.Empty;
    public string DestinationWorldId { get; set; } = string.Empty;
    public Dictionary<string, int> TranslatedStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TranslationNotes { get; set; } = string.Empty;
    public string? TransitionNarrative { get; set; }
    public int SourceLevel { get; set; }
    public Dictionary<string, int> SourceStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result from the AI stat translation prompt, parsed from the narrator's JSON response.
/// </summary>
public class StatTranslationResult
{
    public Dictionary<string, int> TranslatedStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TranslationNotes { get; set; } = string.Empty;
    public string Narrative { get; set; } = string.Empty;
}

/// <summary>
/// World-scoped NPC state. The same canonical NPC can carry different memories and disposition
/// in different worlds or for different players.
/// </summary>
public class WorldNpcState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NpcId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string? PlayerId { get; set; }
    public NpcDispositionState DispositionState { get; set; } = new();
    public List<string>? KnowledgeScopeOverrides { get; set; }
}

/// <summary>
/// Tracks where a player is and when they last visited within a given world.
/// </summary>
public class PlayerWorldState
{
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string CurrentRoomId { get; set; } = WorldDefaults.DefaultSpawnRoomId;
    public bool HasVisited { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }
    public DateTimeOffset? FirstVisitedAt { get; set; }
}