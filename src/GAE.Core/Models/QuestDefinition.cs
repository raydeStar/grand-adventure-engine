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

    /// <summary>Optional room ID where this quest may be offered.</summary>
    public string? QuestGiverRoomId { get; set; }

    /// <summary>NPC ID where the quest is turned in. Defaults to the giver if not set.</summary>
    public string? TurnInNpcId { get; set; }

    /// <summary>Room ID where the quest can be turned in. If null, uses the turn-in NPC's current room.</summary>
    public string? TurnInRoomId { get; set; }

    /// <summary>Minimum player level required to accept this quest.</summary>
    public int MinLevel { get; set; } = 1;

    /// <summary>Minimum NPC disposition intensity required before the quest can be offered.</summary>
    public int? MinDisposition { get; set; }

    /// <summary>Optional player faction requirement to accept this quest.</summary>
    public string? RequiredFaction { get; set; }

    /// <summary>Quest IDs that must be completed before this quest can be offered.</summary>
    public List<string> Prerequisites { get; set; } = [];

    /// <summary>Alias for YAML compatibility with the authored quest scope document.</summary>
    public List<string> RequiredCompletedQuests
    {
        get => Prerequisites;
        set => Prerequisites = value ?? [];
    }

    /// <summary>Whether this quest can only be completed once per player.</summary>
    public bool IsOneTime { get; set; } = true;

    /// <summary>Whether this quest can be accepted again after completion.</summary>
    public bool IsRepeatable
    {
        get => !IsOneTime;
        set => IsOneTime = !value;
    }

    /// <summary>Whether this quest shares progress across multiple players.</summary>
    public bool IsPartyQuest { get; set; }

    /// <summary>Ordered list of stages. The quest advances through these sequentially.</summary>
    public List<QuestStage> Stages { get; set; } = [];

    /// <summary>Rewards granted on quest completion.</summary>
    public QuestReward Rewards { get; set; } = new();

    /// <summary>Alias for YAML compatibility with authored quest documents that use singular reward.</summary>
    public QuestReward Reward
    {
        get => Rewards;
        set => Rewards = value ?? new();
    }

    /// <summary>Tags for filtering and categorization (e.g. "main", "side", "bounty").</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Flavor guidance for the narrator when this quest is first offered.</summary>
    public string? OfferHint { get; set; }

    /// <summary>Flavor guidance for the narrator when this quest is completed.</summary>
    public string? CompletionHint { get; set; }

    /// <summary>Flavor guidance for the narrator when this quest fails.</summary>
    public string? FailureHint { get; set; }
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

    /// <summary>Whether all objectives are required, or any single objective can advance the stage.</summary>
    public bool RequireAllObjectives { get; set; } = true;

    /// <summary>Alias for YAML authored with require_all.</summary>
    public bool RequireAll
    {
        get => RequireAllObjectives;
        set => RequireAllObjectives = value;
    }

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

    /// <summary>Human-readable target name used in journals and narrator prompts.</summary>
    public string? TargetName { get; set; }

    /// <summary>Optional item ID required for Deliver objectives.</summary>
    public string? RequiredItemId { get; set; }

    /// <summary>How many of the target are required (e.g. kill 3 rats, collect 5 herbs).</summary>
    public int RequiredCount { get; set; } = 1;

    /// <summary>Optional room constraint — only counts while the player is in this room.</summary>
    public string? LocationConstraint { get; set; }

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

    /// <summary>Legacy disposition shift applied to the quest giver NPC on completion.</summary>
    public int DispositionShift { get; set; }

    /// <summary>Legacy memory flags applied to the quest giver NPC on completion.</summary>
    public List<string> MemoryFlags { get; set; } = [];

    /// <summary>Targeted NPC disposition shifts applied on completion.</summary>
    public Dictionary<string, int> DispositionShifts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Targeted NPC memory flags applied on completion.</summary>
    public Dictionary<string, string> NpcMemoryFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quest unlocked by completing this quest.</summary>
    public string? UnlockQuestId { get; set; }

    /// <summary>Alias for YAML authored with unlocks_quest.</summary>
    public string? UnlocksQuest
    {
        get => UnlockQuestId;
        set => UnlockQuestId = value;
    }
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
    /// <summary>Escort an NPC or vulnerable target safely through a scene.</summary>
    Escort,
    /// <summary>Enter a specific room.</summary>
    Discover,
    /// <summary>Initiate conversation with a specific NPC.</summary>
    TalkTo,
    /// <summary>Survive for a required number of rounds or encounters.</summary>
    Survive,
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
