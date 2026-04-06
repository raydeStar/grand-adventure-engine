using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.Worlds;

namespace GAE.Engine.Data;

/// <summary>EF Core entity for the worlds table.</summary>
public class WorldEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SpawnRoomId { get; set; } = WorldDefaults.DefaultSpawnRoomId;
    public bool IsActive { get; set; } = true;
    public GameRulesConfig Rules { get; set; } = new();
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<WorldPortal> Portals { get; set; } = [];

    public World ToDomain() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        SpawnRoomId = SpawnRoomId,
        IsActive = IsActive,
        Rules = Rules,
        CreatedBy = CreatedBy,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Tags = Tags,
        Portals = Portals
    };

    public static WorldEntity FromDomain(World world) => new()
    {
        Id = world.Id,
        Name = world.Name,
        Description = world.Description,
        SpawnRoomId = world.SpawnRoomId,
        IsActive = world.IsActive,
        Rules = world.Rules,
        CreatedBy = world.CreatedBy,
        CreatedAt = world.CreatedAt,
        UpdatedAt = world.UpdatedAt,
        Tags = world.Tags,
        Portals = world.Portals
    };

    public void UpdateFrom(World world)
    {
        Name = world.Name;
        Description = world.Description;
        SpawnRoomId = world.SpawnRoomId;
        IsActive = world.IsActive;
        Rules = world.Rules;
        CreatedBy = world.CreatedBy;
        CreatedAt = world.CreatedAt;
        UpdatedAt = world.UpdatedAt;
        Tags = world.Tags;
        Portals = world.Portals;
    }
}

/// <summary>EF Core entity for world-scoped player location metadata.</summary>
public class PlayerWorldStateEntity
{
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string CurrentRoomId { get; set; } = WorldDefaults.DefaultSpawnRoomId;
    public bool HasVisited { get; set; }
    public DateTimeOffset? FirstVisitedAt { get; set; }
    public DateTimeOffset? LastVisitedAt { get; set; }

    public PlayerWorldState ToDomain() => new()
    {
        PlayerId = PlayerId,
        WorldId = WorldId,
        CurrentRoomId = CurrentRoomId,
        HasVisited = HasVisited,
        FirstVisitedAt = FirstVisitedAt,
        LastVisitedAt = LastVisitedAt
    };

    public static PlayerWorldStateEntity FromDomain(PlayerWorldState state) => new()
    {
        PlayerId = state.PlayerId,
        WorldId = state.WorldId,
        CurrentRoomId = state.CurrentRoomId,
        HasVisited = state.HasVisited,
        FirstVisitedAt = state.FirstVisitedAt,
        LastVisitedAt = state.LastVisitedAt
    };

    public void UpdateFrom(PlayerWorldState state)
    {
        CurrentRoomId = state.CurrentRoomId;
        HasVisited = state.HasVisited;
        FirstVisitedAt = state.FirstVisitedAt;
        LastVisitedAt = state.LastVisitedAt;
    }
}

/// <summary>EF Core entity for frozen per-world stat snapshots.</summary>
public class WorldStatSnapshotEntity
{
    public string Id { get; set; } = string.Empty;
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
    public DateTimeOffset CreatedAt { get; set; }

    public WorldStatSnapshot ToDomain() => new()
    {
        Id = Id,
        PlayerId = PlayerId,
        WorldId = WorldId,
        Stats = Stats,
        Class = Class,
        Race = Race,
        Level = Level,
        Hp = Hp,
        MaxHp = MaxHp,
        Mp = Mp,
        MaxMp = MaxMp,
        CreatedAt = CreatedAt
    };

    public static WorldStatSnapshotEntity FromDomain(WorldStatSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        PlayerId = snapshot.PlayerId,
        WorldId = snapshot.WorldId,
        Stats = snapshot.Stats,
        Class = snapshot.Class,
        Race = snapshot.Race,
        Level = snapshot.Level,
        Hp = snapshot.Hp,
        MaxHp = snapshot.MaxHp,
        Mp = snapshot.Mp,
        MaxMp = snapshot.MaxMp,
        CreatedAt = snapshot.CreatedAt
    };

    public void UpdateFrom(WorldStatSnapshot snapshot)
    {
        Stats = snapshot.Stats;
        Class = snapshot.Class;
        Race = snapshot.Race;
        Level = snapshot.Level;
        Hp = snapshot.Hp;
        MaxHp = snapshot.MaxHp;
        Mp = snapshot.Mp;
        MaxMp = snapshot.MaxMp;
        CreatedAt = snapshot.CreatedAt;
    }
}

/// <summary>EF Core entity for cached player stat translations between worlds.</summary>
public class StatTranslationHistoryEntity
{
    public string Id { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string SourceWorldId { get; set; } = string.Empty;
    public string DestinationWorldId { get; set; } = string.Empty;
    public Dictionary<string, int> TranslatedStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TranslationNotes { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public StatTranslationHistory ToDomain() => new()
    {
        Id = Id,
        PlayerId = PlayerId,
        SourceWorldId = SourceWorldId,
        DestinationWorldId = DestinationWorldId,
        TranslatedStats = TranslatedStats,
        TranslationNotes = TranslationNotes,
        CreatedAt = CreatedAt
    };

    public static StatTranslationHistoryEntity FromDomain(StatTranslationHistory history) => new()
    {
        Id = history.Id,
        PlayerId = history.PlayerId,
        SourceWorldId = history.SourceWorldId,
        DestinationWorldId = history.DestinationWorldId,
        TranslatedStats = history.TranslatedStats,
        TranslationNotes = history.TranslationNotes,
        CreatedAt = history.CreatedAt
    };

    public void UpdateFrom(StatTranslationHistory history)
    {
        TranslatedStats = history.TranslatedStats;
        TranslationNotes = history.TranslationNotes;
        CreatedAt = history.CreatedAt;
    }
}

/// <summary>EF Core entity for world-specific NPC state.</summary>
public class WorldNpcStateEntity
{
    public string Id { get; set; } = string.Empty;
    public string NpcId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string? PlayerId { get; set; }
    public NpcDispositionState DispositionState { get; set; } = new();
    public List<string>? KnowledgeScopeOverrides { get; set; }

    public WorldNpcState ToDomain() => new()
    {
        Id = Id,
        NpcId = NpcId,
        WorldId = WorldId,
        PlayerId = PlayerId,
        DispositionState = DispositionState,
        KnowledgeScopeOverrides = KnowledgeScopeOverrides
    };

    public static WorldNpcStateEntity FromDomain(WorldNpcState state) => new()
    {
        Id = state.Id,
        NpcId = state.NpcId,
        WorldId = state.WorldId,
        PlayerId = state.PlayerId,
        DispositionState = state.DispositionState,
        KnowledgeScopeOverrides = state.KnowledgeScopeOverrides
    };

    public void UpdateFrom(WorldNpcState state)
    {
        DispositionState = state.DispositionState;
        KnowledgeScopeOverrides = state.KnowledgeScopeOverrides;
    }
}