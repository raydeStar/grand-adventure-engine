using GAE.Core.Models;

namespace GAE.Engine.Data;

/// <summary>EF Core entity for the players table. Maps 1:1 with PlayerCharacter domain model.</summary>
public class PlayerEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Faction { get; set; } = "neutral";
    public string Backstory { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public long? ThreadId { get; set; }
    public bool HasCompletedDemo { get; set; }
    public string CurrentRoomId { get; set; } = "spawn";

    // Resources
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Mp { get; set; }
    public int MaxMp { get; set; }
    public int Gold { get; set; }
    public int Xp { get; set; }
    public int Level { get; set; } = 1;

    // Attributes
    public int Str { get; set; } = 10;
    public int Dex { get; set; } = 10;
    public int Con { get; set; } = 10;
    public int Int { get; set; } = 10;
    public int Wis { get; set; } = 10;
    public int Cha { get; set; } = 10;
    public int Luck { get; set; } = 10;

    // JSONB columns for complex nested structures
    public EquipmentLoadout Equipment { get; set; } = new();
    public List<InventoryItem> Inventory { get; set; } = [];
    public List<StatusEffect> StatusEffects { get; set; } = [];
    public List<LearnedSpell> Spellbook { get; set; } = [];
    public List<QuestProgress> QuestLog { get; set; } = [];
    public InteractionState Interaction { get; set; } = new();

    // Timestamps
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }

    /// <summary>Convert to domain model.</summary>
    public PlayerCharacter ToDomain() => new()
    {
        Id = Id,
        Name = Name,
        Race = Race,
        Class = Class,
        Faction = Faction,
        Backstory = Backstory,
        DiscordId = DiscordId,
        ThreadId = ThreadId is not null ? (ulong)ThreadId.Value : null,
        HasCompletedDemo = HasCompletedDemo,
        CurrentRoomId = CurrentRoomId,
        Hp = Hp,
        MaxHp = MaxHp,
        Mp = Mp,
        MaxMp = MaxMp,
        Gold = Gold,
        Xp = Xp,
        Level = Level,
        Str = Str,
        Dex = Dex,
        Con = Con,
        Int = Int,
        Wis = Wis,
        Cha = Cha,
        Luck = Luck,
        Equipment = Equipment,
        Inventory = Inventory,
        StatusEffects = StatusEffects,
        Spellbook = Spellbook,
        QuestLog = QuestLog,
        Interaction = Interaction,
        CreatedAt = CreatedAt,
        LastActiveAt = LastActiveAt
    };

    /// <summary>Create from domain model.</summary>
    public static PlayerEntity FromDomain(PlayerCharacter p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Race = p.Race,
        Class = p.Class,
        Faction = p.Faction,
        Backstory = p.Backstory,
        DiscordId = p.DiscordId,
        ThreadId = p.ThreadId is not null ? (long)p.ThreadId.Value : null,
        HasCompletedDemo = p.HasCompletedDemo,
        CurrentRoomId = p.CurrentRoomId,
        Hp = p.Hp,
        MaxHp = p.MaxHp,
        Mp = p.Mp,
        MaxMp = p.MaxMp,
        Gold = p.Gold,
        Xp = p.Xp,
        Level = p.Level,
        Str = p.Str,
        Dex = p.Dex,
        Con = p.Con,
        Int = p.Int,
        Wis = p.Wis,
        Cha = p.Cha,
        Luck = p.Luck,
        Equipment = p.Equipment,
        Inventory = p.Inventory,
        StatusEffects = p.StatusEffects,
        Spellbook = p.Spellbook,
        QuestLog = p.QuestLog,
        Interaction = p.Interaction,
        CreatedAt = p.CreatedAt,
        LastActiveAt = p.LastActiveAt
    };

    /// <summary>Update an existing entity from a domain model (avoids allocating a new entity).</summary>
    public void UpdateFrom(PlayerCharacter p)
    {
        Name = p.Name;
        Race = p.Race;
        Class = p.Class;
        Faction = p.Faction;
        Backstory = p.Backstory;
        DiscordId = p.DiscordId;
        ThreadId = p.ThreadId is not null ? (long)p.ThreadId.Value : null;
        HasCompletedDemo = p.HasCompletedDemo;
        CurrentRoomId = p.CurrentRoomId;
        Hp = p.Hp;
        MaxHp = p.MaxHp;
        Mp = p.Mp;
        MaxMp = p.MaxMp;
        Gold = p.Gold;
        Xp = p.Xp;
        Level = p.Level;
        Str = p.Str;
        Dex = p.Dex;
        Con = p.Con;
        Int = p.Int;
        Wis = p.Wis;
        Cha = p.Cha;
        Luck = p.Luck;
        Equipment = p.Equipment;
        Inventory = p.Inventory;
        StatusEffects = p.StatusEffects;
        Spellbook = p.Spellbook;
        QuestLog = p.QuestLog;
        Interaction = p.Interaction;
        CreatedAt = p.CreatedAt;
        LastActiveAt = p.LastActiveAt;
    }
}

/// <summary>EF Core entity for the rooms table (templates and discovered rooms).</summary>
public class RoomEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsTemplate { get; set; } = true;

    // JSONB columns
    public Dictionary<string, string> Exits { get; set; } = new();
    public List<Npc> Npcs { get; set; } = [];
    public List<InventoryItem> Items { get; set; } = [];
    public List<string> EnvironmentTags { get; set; } = [];

    public bool IsDiscovered { get; set; }
    public string? AsciiArt { get; set; }
    public DateTimeOffset? DiscoveredAt { get; set; }

    public Room ToDomain() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Exits = Exits,
        Npcs = Npcs,
        Items = Items,
        EnvironmentTags = EnvironmentTags,
        IsDiscovered = IsDiscovered,
        AsciiArt = AsciiArt,
        DiscoveredAt = DiscoveredAt
    };

    public static RoomEntity FromDomain(Room r, bool isTemplate = true) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Description = r.Description,
        IsTemplate = isTemplate,
        Exits = r.Exits,
        Npcs = r.Npcs,
        Items = r.Items,
        EnvironmentTags = r.EnvironmentTags,
        IsDiscovered = r.IsDiscovered,
        AsciiArt = r.AsciiArt,
        DiscoveredAt = r.DiscoveredAt
    };

    public void UpdateFrom(Room r)
    {
        Name = r.Name;
        Description = r.Description;
        Exits = r.Exits;
        Npcs = r.Npcs;
        Items = r.Items;
        EnvironmentTags = r.EnvironmentTags;
        IsDiscovered = r.IsDiscovered;
        AsciiArt = r.AsciiArt;
        DiscoveredAt = r.DiscoveredAt;
    }
}

/// <summary>EF Core entity for per-player room instances.</summary>
public class PlayerRoomEntity
{
    public string Id { get; set; } = string.Empty; // "playerId:roomId"
    public string PlayerId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // JSONB columns
    public Dictionary<string, string> Exits { get; set; } = new();
    public List<Npc> Npcs { get; set; } = [];
    public List<InventoryItem> Items { get; set; } = [];
    public List<string> EnvironmentTags { get; set; } = [];

    public bool IsDiscovered { get; set; }
    public string? AsciiArt { get; set; }
    public DateTimeOffset? DiscoveredAt { get; set; }

    public Room ToDomain() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Exits = Exits,
        Npcs = Npcs,
        Items = Items,
        EnvironmentTags = EnvironmentTags,
        IsDiscovered = IsDiscovered,
        AsciiArt = AsciiArt,
        DiscoveredAt = DiscoveredAt
    };

    public static PlayerRoomEntity FromDomain(Room r, string playerId, string roomId) => new()
    {
        Id = r.Id,
        PlayerId = playerId,
        RoomId = roomId,
        Name = r.Name,
        Description = r.Description,
        Exits = r.Exits,
        Npcs = r.Npcs,
        Items = r.Items,
        EnvironmentTags = r.EnvironmentTags,
        IsDiscovered = r.IsDiscovered,
        AsciiArt = r.AsciiArt,
        DiscoveredAt = r.DiscoveredAt
    };

    public void UpdateFrom(Room r)
    {
        Name = r.Name;
        Description = r.Description;
        Exits = r.Exits;
        Npcs = r.Npcs;
        Items = r.Items;
        EnvironmentTags = r.EnvironmentTags;
        IsDiscovered = r.IsDiscovered;
        AsciiArt = r.AsciiArt;
        DiscoveredAt = r.DiscoveredAt;
    }
}

/// <summary>EF Core entity for the story_entries table.</summary>
public class StoryEntryEntity
{
    public string Id { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string RawInput { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string MechanicalSummary { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }

    public StoryEntry ToDomain() => new()
    {
        Id = Id,
        ActionId = ActionId,
        RawInput = RawInput,
        PlayerId = PlayerId,
        RoomId = RoomId,
        MechanicalSummary = MechanicalSummary,
        Narration = Narration,
        Timestamp = Timestamp
    };

    public static StoryEntryEntity FromDomain(StoryEntry e) => new()
    {
        Id = e.Id,
        ActionId = e.ActionId,
        RawInput = e.RawInput,
        PlayerId = e.PlayerId,
        RoomId = e.RoomId,
        MechanicalSummary = e.MechanicalSummary,
        Narration = e.Narration,
        Timestamp = e.Timestamp
    };
}

/// <summary>EF Core entity for stored combat states (JSONB blob keyed by room).</summary>
public class CombatStateEntity
{
    public string RoomId { get; set; } = string.Empty;
    public CombatState State { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF Core entity for the game_events audit log (replaces journal.jsonl).</summary>
public class GameEventEntity
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public int Type { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string? RoomId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Narration { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>EF Core entity for LLM conversation logs (replaces conversations.jsonl).</summary>
public class ConversationLogEntity
{
    public long Id { get; set; }
    public string LogId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? PlayerId { get; set; }
    public string? RoomId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public long LatencyMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>EF Core entity for shared party quest state.</summary>
public class PartyQuestEntity
{
    public string GroupId { get; set; } = string.Empty;
    public PartyQuestProgress State { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
