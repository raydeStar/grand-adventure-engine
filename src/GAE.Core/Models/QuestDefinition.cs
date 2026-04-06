using System.Text.Json.Serialization;
using GAE.Core.Registry;

namespace GAE.Core.Models;

/// <summary>
/// A quest definition authored in YAML and loaded into the content registry at boot.
/// Defines the quest's structure, stages, objectives, and rewards. The narrator enriches
/// descriptions at runtime; authored text serves as the skeleton and fallback.
/// </summary>
public class QuestDefinition : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>NPC ID that offers this quest. Must be present in the room for the player to receive the offer.</summary>
    public string GiverId { get; set; } = string.Empty;

    /// <summary>NPC ID where the quest is turned in. Defaults to the giver if not set.</summary>
    public string? TurnInNpcId { get; set; }

    /// <summary>Room ID where the quest can be turned in. If null, uses the turn-in NPC's current room.</summary>
    public string? TurnInRoomId { get; set; }

    /// <summary>Minimum player level required to accept this quest.</summary>
    public int MinLevel { get; set; } = 1;

    /// <summary>Quest IDs that must be completed before this quest can be offered.</summary>
    public List<string> Prerequisites { get; set; } = [];

    /// <summary>Whether this quest can only be completed once per player.</summary>
    public bool IsOneTime { get; set; } = true;

    /// <summary>Reserved for future party quest support. Not implemented in v1.</summary>
    public bool IsPartyQuest { get; set; }

    /// <summary>Ordered list of stages. The quest advances through these sequentially.</summary>
    public List<QuestStage> Stages { get; set; } = [];

    /// <summary>Rewards granted on quest completion.</summary>
    public QuestReward Rewards { get; set; } = new();

    /// <summary>Tags for filtering and categorization (e.g. "main", "side", "bounty").</summary>
    public List<string> Tags { get; set; } = [];
}

/// <summary>
/// A discrete phase of a quest. Each stage has one or more objectives that must all
/// be completed before the quest advances to the next stage.
/// </summary>
public class QuestStage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Objectives that must all be completed to clear this stage.</summary>
    public List<QuestObjective> Objectives { get; set; } = [];

    /// <summary>ID of the next stage. Null means this is the final stage — completing it completes the quest.</summary>
    public string? NextStageId { get; set; }

    /// <summary>Optional narration hint for the narrator when this stage begins.</summary>
    public string? NarratorHint { get; set; }
}

/// <summary>
/// A single trackable objective within a quest stage. Deterministic objectives are
/// evaluated mechanically by the QuestTracker; Custom objectives are evaluated
/// by the narrator on structured response paths.
/// </summary>
public class QuestObjective
{
    public string Id { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The type of objective — determines how progress is tracked.</summary>
    public ObjectiveType Type { get; set; }

    /// <summary>Target identifier: monster ID for Kill, item ID for Collect/Deliver, room ID for Discover, NPC ID for TalkTo.</summary>
    public string? TargetId { get; set; }

    /// <summary>How many of the target are required (e.g. kill 3 rats, collect 5 herbs).</summary>
    public int RequiredCount { get; set; } = 1;

    /// <summary>For Custom objectives: a short description the narrator uses to evaluate completion.</summary>
    public string? CustomCondition { get; set; }
}

/// <summary>Rewards granted when a quest is completed and turned in.</summary>
public class QuestReward
{
    public int Xp { get; set; }
    public int Gold { get; set; }

    /// <summary>Item template IDs to grant on completion.</summary>
    public List<QuestRewardItem> Items { get; set; } = [];

    /// <summary>Disposition shift applied to the quest giver NPC on completion.</summary>
    public int DispositionShift { get; set; }

    /// <summary>Memory flags to add to the quest giver NPC on completion.</summary>
    public List<string> MemoryFlags { get; set; } = [];
}

/// <summary>An item reward referencing a registered item template.</summary>
public class QuestRewardItem
{
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

/// <summary>Determines how an objective's progress is tracked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectiveType
{
    /// <summary>Kill a specific monster type.</summary>
    Kill,
    /// <summary>Collect a specific item into inventory.</summary>
    Collect,
    /// <summary>Deliver an item to a specific NPC.</summary>
    Deliver,
    /// <summary>Enter a specific room.</summary>
    Discover,
    /// <summary>Initiate conversation with a specific NPC.</summary>
    TalkTo,
    /// <summary>AI-evaluated objective — checked on structured narrator response paths only.</summary>
    Custom
}

/// <summary>The overall status of a quest in a player's quest log.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestStatus
{
    /// <summary>Quest has been offered but not yet accepted.</summary>
    Available,
    /// <summary>Quest is actively being pursued.</summary>
    Active,
    /// <summary>All objectives complete — ready to turn in.</summary>
    ReadyToTurnIn,
    /// <summary>Quest has been completed and rewards collected.</summary>
    Completed,
    /// <summary>Quest was abandoned by the player.</summary>
    Abandoned,
    /// <summary>Quest failed due to authored failure conditions.</summary>
    Failed
}
