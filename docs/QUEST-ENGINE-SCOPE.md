# Quest Engine — Project Scope Document

**Project:** Grand Adventure Engine (GAE)
**Author:** Mark Hall / Sir Thaddeus
**Date:** 2026-04-05
**Status:** Draft — Ready for GHC Review

---

## 1. Executive Summary

The Quest Engine adds a structured quest system to the Grand Adventure Engine. Quests are **hybrid-authored**: designers define quest skeletons in YAML (objectives, stages, rewards, triggers), and the AI narrator weaves them into the living story with dynamic flavor text, NPC dialogue, and contextual delivery.

The system supports both **simple single-objective quests** (fetch, kill, deliver) and **multi-stage quest chains** with branching objectives, prerequisites, and world-altering consequences. For v1, only simple quests will ship as authored content, but the underlying data model supports full chain complexity from day one.

Quest progress is tracked **per-player** for solo quests and **per-party** for shared quests, leveraging the existing per-player state cloning pattern.

---

## 2. Goals and Non-Goals

### Goals

- Data-driven quest definitions in YAML, loaded through the existing `IContentRegistry<T>` pattern
- A flexible `QuestDefinition` model supporting single-objective and multi-stage quests
- Per-player quest log with active, completed, failed, and abandoned states
- Party quest support where multiple players share progress on the same quest
- NPC quest-giver integration via new fields on the `Npc` model
- AI narrator integration: the narrator presents quests in-character, never as UI chrome
- Quest objective types: kill, fetch/collect, deliver, escort, discover (location), talk-to, survive, custom
- Reward distribution: XP, gold, items, reputation, disposition shifts, unlocked content
- Quest prerequisite chains (complete Quest A before Quest B becomes available)
- Automatic objective tracking hooks in existing engine actions (kill tracking on combat resolution, item tracking on pickup, location tracking on room entry)
- `quest` / `journal` player commands to review active and completed quests
- YAML seed content for 5-8 starter quests covering each objective type

### Non-Goals (out of scope for v1)

- Procedurally generated quests (AI invents entire quests from scratch) — future phase
- Quest UI beyond the text log and journal command (no graphical quest tracker)
- Timed/expiring quests with real-world deadlines
- Quest difficulty scaling based on player level (rewards are fixed per definition)
- Quest sharing/trading between players
- Branching dialogue trees with UI choices (narrator handles dialogue naturally)

---

## 3. Architecture Overview

### 3.1 Where It Fits

```
┌─────────────────────────────────────────────────┐
│                   GameEngine                     │
│  ┌───────────┐  ┌───────────┐  ┌─────────────┐  │
│  │  Combat    │  │  Trading  │  │  Quest       │  │
│  │  Handler   │  │  Handler  │  │  Engine      │  │
│  └───────────┘  └───────────┘  └──────┬──────┘  │
│                                       │          │
│  ┌────────────────────────────────────┤          │
│  │          QuestTracker              │          │
│  │  (objective hooks, progress eval)  │          │
│  └────────────────────────────────────┘          │
└───────────────┬─────────────────────────────────┘
                │
    ┌───────────┴───────────┐
    │    IStateManager      │    ┌──────────────────┐
    │  (quest persistence)  │    │  IContentRegistry │
    └───────────────────────┘    │  <QuestDefinition>│
                                 │  (YAML-seeded)    │
                                 └──────────────────┘
```

### 3.2 New Files and Modifications

**New files (GAE.Core):**

| File | Purpose |
|---|---|
| `Models/Quest.cs` | `QuestDefinition`, `QuestStage`, `QuestObjective`, `QuestReward` models |
| `Models/QuestProgress.cs` | `QuestProgress`, `ObjectiveProgress`, `QuestStatus` enum |
| `Models/PartyQuestProgress.cs` | Shared quest state for party-based quests |
| `Registry/QuestDefinition.cs` | `IRegistryEntry` implementation for quest content registry |
| `Interfaces/IQuestTracker.cs` | Interface for objective tracking and quest state evaluation |

**New files (GAE.Engine):**

| File | Purpose |
|---|---|
| `QuestEngine.cs` | Core quest logic: accept, advance, complete, fail, abandon |
| `QuestTracker.cs` | Hooks into engine events (combat kills, item pickups, room entry) to auto-advance objectives |
| `Registry/QuestSeedLoader.cs` | Loads quest YAML into `IContentRegistry<QuestDefinition>` |

**New files (GAE.Narrator):**

| File | Purpose |
|---|---|
| `Prompts/QuestPrompts.cs` | System prompts for quest offer, progress update, completion, and failure narration |

**New files (Config):**

| File | Purpose |
|---|---|
| `config/quests.yaml` | Quest definitions for v1 starter content |

**Modified files:**

| File | Change |
|---|---|
| `Models/PlayerCharacter.cs` | Add `List<QuestProgress> QuestLog` property |
| `Models/Npc.cs` | Add `List<string> QuestsOffered` and `QuestGiverPriority` fields |
| `Models/InteractionState.cs` | No new mode needed — quest interactions flow through `Conversation` mode |
| `Models/GameAction.cs` | Add `ActionType.Quest` and `ActionType.Journal` to the enum |
| `Engine/GameEngine.cs` | Wire quest tracker hooks into combat resolution, item pickup, room entry, and NPC conversation handlers |
| `Engine/CommandParser.cs` | Parse `quest`, `journal`, `accept`, `abandon` commands |
| `Engine/Registry/RegistrySeedLoader.cs` | Add quest YAML loading |
| `Interfaces/IStateManager.cs` | Add quest progress CRUD methods |
| `State/StateManager.cs` | Implement quest progress persistence (journal to disk) |
| `config/lore-seed.yaml` | Add `quests_offered` fields to existing NPC definitions |

---

## 4. Data Models

### 4.1 QuestDefinition (registry entry, YAML-seeded)

```csharp
public class QuestDefinition : IRegistryEntry
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string? Description { get; set; }

    // Who gives this quest and where
    public string QuestGiverId { get; set; }         // NPC ID
    public string? QuestGiverRoomId { get; set; }    // Optional: only offered in this room
    public string? TurnInNpcId { get; set; }         // NPC to complete quest with (defaults to giver)

    // Prerequisites
    public List<string> RequiredCompletedQuests { get; set; } = [];
    public int MinLevel { get; set; } = 1;
    public int? MinDisposition { get; set; }       // Min NPC disposition to offer (null = no gate)
    public string? RequiredFaction { get; set; }

    // Stages (for multi-stage quests; simple quests have exactly one stage)
    public List<QuestStage> Stages { get; set; } = [];

    // Rewards granted on completion
    public QuestReward Reward { get; set; } = new();

    // Is this a party quest or solo?
    public bool IsPartyQuest { get; set; } = false;

    // Tags for narrator context ("main_story", "side_quest", "faction_quest", etc.)
    public List<string> Tags { get; set; } = [];

    // Can this quest be repeated after completion?
    public bool IsRepeatable { get; set; } = false;

    // Narrator flavor hints (the AI uses these, not displays them verbatim)
    public string? OfferHint { get; set; }       // Mood/tone hint for quest offer narration
    public string? CompletionHint { get; set; }  // Mood/tone hint for completion narration
    public string? FailureHint { get; set; }     // What happens if the quest fails
}
```

### 4.2 QuestStage

```csharp
public class QuestStage
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string? NarratorHint { get; set; }    // Flavor guidance for the AI narrator
    public List<QuestObjective> Objectives { get; set; } = [];
    public bool RequireAllObjectives { get; set; } = true;  // AND vs OR logic
    public string? NextStageId { get; set; }      // null = final stage
}
```

### 4.3 QuestObjective

```csharp
public class QuestObjective
{
    public string Id { get; set; }
    public ObjectiveType Type { get; set; }
    public string Description { get; set; }       // For journal display

    // Type-specific target
    public string? TargetId { get; set; }          // NPC ID, Item ID, Room ID, Monster template ID
    public string? TargetName { get; set; }        // Fallback display name
    public int RequiredCount { get; set; } = 1;    // Kill 5 wolves, collect 3 herbs

    // Optional: only counts if done in this room
    public string? LocationConstraint { get; set; }

    // For "custom" objectives resolved by the narrator via AI judgment
    public string? CustomCondition { get; set; }
}

public enum ObjectiveType
{
    Kill,           // Defeat a specific monster or NPC
    Collect,        // Pick up / possess specific items
    Deliver,        // Bring item to NPC
    Escort,         // Keep NPC alive while traversing rooms
    Discover,       // Enter a specific room
    TalkTo,         // Have a conversation with a specific NPC
    Survive,        // Survive N combat rounds or reach a location alive
    Custom          // Free-form — narrator AI evaluates completion
}
```

### 4.4 QuestReward

```csharp
public class QuestReward
{
    public int Xp { get; set; }
    public int Gold { get; set; }
    public List<string> ItemIds { get; set; } = [];          // Item template IDs
    public Dictionary<string, int> DispositionShifts { get; set; } = new(); // NPC ID → shift
    public string? UnlocksQuestId { get; set; }              // Chain: completing this unlocks another
    public Dictionary<string, string> NpcMemoryFlags { get; set; } = new(); // NPC ID → flag to add
}
```

### 4.5 QuestProgress (per-player state)

```csharp
public class QuestProgress
{
    public string QuestId { get; set; }
    public QuestStatus Status { get; set; } = QuestStatus.Active;
    public string CurrentStageId { get; set; }
    public Dictionary<string, ObjectiveProgress> Objectives { get; set; } = new();
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? PartyQuestGroupId { get; set; }  // Links to shared party state if applicable
}

public class ObjectiveProgress
{
    public string ObjectiveId { get; set; }
    public int CurrentCount { get; set; }
    public bool IsComplete { get; set; }
}

public enum QuestStatus
{
    Active,
    Completed,
    Failed,
    Abandoned
}
```

---

## 5. Quest Lifecycle

### 5.1 Discovery and Offer

1. Player enters a room or talks to an NPC who has `QuestsOffered` entries
2. The `QuestEngine` checks prerequisites (completed quests, level, faction)
3. If eligible, the quest ID is injected into the narrator's conversation prompt
4. The narrator offers the quest in-character — no system UI, pure narrative
5. Player accepts via natural language ("I'll do it", "sure", "accept") or the `accept` command

### 5.2 Active Tracking

1. On accept, `QuestProgress` is created and added to `PlayerCharacter.QuestLog`
2. `QuestTracker` registers hooks for the current stage's objective types:
   - **Kill:** listens to combat resolution events → increments kill count
   - **Collect:** listens to inventory changes → checks item possession
   - **Deliver:** listens to NPC conversation start → checks if player has required items
   - **Discover:** listens to room entry → checks room ID match
   - **TalkTo:** listens to conversation initiation → checks NPC ID match
   - **Survive:** listens to combat round completion → increments survive counter
   - **Custom:** on each narrator turn, includes the custom condition in the prompt; narrator returns a structured JSON flag indicating whether the condition is met
3. When an objective's `CurrentCount >= RequiredCount`, it's marked complete
4. When all objectives in a stage are complete (or any, if `RequireAllObjectives = false`), the stage advances

### 5.3 Completion

1. Final stage objectives are all met
2. If `TurnInNpcId` is set, player must talk to that NPC to trigger completion
3. If no turn-in NPC, quest completes automatically when final objective is met
4. `QuestEngine` distributes rewards (XP, gold, items, disposition shifts, memory flags)
5. Narrator delivers the completion narration using `CompletionHint`
6. If `UnlocksQuestId` is set, the next quest becomes available

### 5.4 Failure and Abandonment

- **Failure:** certain quests can fail if conditions are met (escort target dies, key NPC killed). The `QuestTracker` monitors failure conditions and sets `QuestStatus.Failed`
- **Abandonment:** player uses `abandon [quest name]` command. Progress is lost. If `IsRepeatable`, the quest can be re-accepted later

### 5.5 Party Quest Flow

1. When a player accepts a party quest, a `PartyQuestGroupId` is generated
2. Other players in the same room can join via `accept [quest name]`
3. All joined players share a single `PartyQuestProgress` record
4. Objective progress is pooled (Player A kills 2 wolves + Player B kills 3 = 5/5)
5. Rewards are distributed to all participants on completion

---

## 6. YAML Schema (config/quests.yaml)

```yaml
quests:
  - id: "rats_in_the_cellar"
    name: "Rats in the Cellar"
    description: "The innkeeper has a rodent problem. A classic."
    quest_giver: "npc_innkeeper_marta"
    turn_in_npc: "npc_innkeeper_marta"
    min_level: 1
    is_party_quest: false
    tags: ["side_quest", "starter"]
    offer_hint: "Marta is embarrassed but desperate. She offers this reluctantly."
    completion_hint: "Marta is genuinely grateful — she pours you a free drink."
    stages:
      - id: "kill_rats"
        name: "Clear the Cellar"
        narrator_hint: "The cellar is dark, damp, and skittering with oversized rats."
        objectives:
          - id: "kill_giant_rats"
            type: kill
            target_name: "Giant Rat"
            target_id: "monster_giant_rat"
            required_count: 5
            description: "Slay 5 giant rats in the cellar"
    reward:
      xp: 50
      gold: 25
      items: ["item_minor_healing_potion"]
      disposition_shifts:
        npc_innkeeper_marta: 15

  - id: "the_missing_pendant"
    name: "The Missing Pendant"
    description: "A merchant's heirloom has gone missing. There are whispers of thieves."
    quest_giver: "npc_merchant_aldric"
    turn_in_npc: "npc_merchant_aldric"
    min_level: 3
    required_completed_quests: []
    is_party_quest: true
    tags: ["side_quest", "investigation"]
    offer_hint: "Aldric is visibly upset, wringing his hands. The pendant was his mother's."
    completion_hint: "Aldric's eyes well with tears. He insists on overpaying you."
    failure_hint: "If the pendant is destroyed or lost, Aldric sinks into despair."
    stages:
      - id: "investigate"
        name: "Ask Around"
        narrator_hint: "The townsfolk are tight-lipped. Someone knows something."
        objectives:
          - id: "talk_to_guard"
            type: talk_to
            target_id: "npc_guard_brennan"
            target_name: "Guard Brennan"
            description: "Speak with Guard Brennan about the theft"
          - id: "talk_to_beggar"
            type: talk_to
            target_id: "npc_beggar_syl"
            target_name: "Old Syl"
            description: "Ask Old Syl what she saw that night"
        require_all: false  # Either NPC can point you to the hideout
        next_stage: "retrieve"
      - id: "retrieve"
        name: "Retrieve the Pendant"
        narrator_hint: "The thieves' den reeks of smoke and cheap wine."
        objectives:
          - id: "find_pendant"
            type: collect
            target_id: "item_aldrics_pendant"
            target_name: "Aldric's Pendant"
            required_count: 1
            description: "Recover Aldric's pendant from the thieves"
        next_stage: "return"
      - id: "return"
        name: "Return to Aldric"
        objectives:
          - id: "deliver_pendant"
            type: deliver
            target_id: "npc_merchant_aldric"
            target_name: "Merchant Aldric"
            description: "Return the pendant to Aldric"
    reward:
      xp: 150
      gold: 75
      items: ["item_silver_ring"]
      disposition_shifts:
        npc_merchant_aldric: 25
      npc_memory_flags:
        npc_merchant_aldric: "!pendant-returned"
      unlocks_quest: "aldrics_favor"
```

---

## 7. Narrator Integration

The quest engine does **not** present quests through UI — everything flows through the AI narrator. This requires careful prompt injection at key moments.

### 7.1 Prompt Injection Points

| Moment | Injected Context | Narrator Instruction |
|---|---|---|
| **Quest available** (player talks to NPC with eligible quest) | Quest name, description, `OfferHint`, objectives summary | "Work this quest offer naturally into the conversation. Do NOT say 'Quest Available' — the NPC has a problem and is asking for help." |
| **Objective progress** (kill count updated, item found, etc.) | Updated objective state, stage `NarratorHint` | "Acknowledge this progress naturally. A 3/5 kill count might be 'The vermin are thinning...' not 'You have killed 3 of 5 rats.'" |
| **Stage transition** | New stage name, objectives, `NarratorHint` | "The situation has evolved. Narrate the transition dramatically." |
| **Quest complete** | `CompletionHint`, rewards to grant | "Narrate the conclusion. Describe the NPC's reaction. Mention rewards earned naturally (gold pouch, XP gained as 'you feel wiser')." |
| **Quest failed** | `FailureHint`, consequences | "This quest has failed. Narrate the consequence with weight — actions have meaning here." |

### 7.2 Narrator Response Schema Extension

The narrator's structured JSON response (already used for disposition updates, stat changes, etc.) gains a new optional field:

```json
{
  "narrative": "...",
  "quest_updates": {
    "accepted": ["rats_in_the_cellar"],
    "custom_objective_met": { "objective_id": true },
    "declined": ["rats_in_the_cellar"]
  }
}
```

This lets the narrator signal quest acceptance/decline through conversation without explicit player commands.

---

## 8. Player Commands

| Command | Aliases | Description |
|---|---|---|
| `quest` / `journal` | `quests`, `log`, `j` | Display active quests with current objective progress |
| `quest [name]` | — | Show detailed status of a specific quest |
| `accept [quest]` | `take` | Accept an offered quest explicitly |
| `abandon [quest]` | `drop quest` | Abandon an active quest |
| `completed` | `done`, `finished` | List completed quests |

---

## 9. Integration Hooks in GameEngine

These are the **specific touchpoints** where the quest tracker taps into existing engine flows:

### 9.1 Combat Resolution (in `ProcessCombatTurnAsync`)

After enemy death:
```
QuestTracker.OnEnemyKilled(playerId, enemyTemplateId, roomId)
→ scans active quests for Kill objectives matching the enemy
→ increments ObjectiveProgress.CurrentCount
→ returns list of updated objectives for narrator context
```

### 9.2 Item Acquisition (in inventory mutation handlers)

After item added to inventory:
```
QuestTracker.OnItemAcquired(playerId, itemId, itemCount)
→ scans for Collect objectives matching the item
→ updates count based on current inventory total
```

### 9.3 Room Entry (in `ProcessMoveAsync`)

After successful room transition:
```
QuestTracker.OnRoomEntered(playerId, roomId)
→ scans for Discover objectives matching the room
→ marks objective complete if matched
```

### 9.4 Conversation Start (in `ProcessConversationTurnAsync`)

On entering conversation with NPC:
```
QuestTracker.OnConversationStarted(playerId, npcId)
→ scans for TalkTo objectives matching the NPC
→ scans for Deliver objectives (check if player has required item)
→ checks if NPC has available quests to offer (inject into prompt)
```

---

## 10. State Persistence

Quest state follows the existing **journal-to-disk** pattern (no database). The `IStateManager` gets these new methods:

```csharp
// Quest progress CRUD
Task<List<QuestProgress>> GetQuestLogAsync(string playerId);
Task SaveQuestProgressAsync(string playerId, QuestProgress progress);
Task RemoveQuestProgressAsync(string playerId, string questId);

// Party quest state
Task<PartyQuestProgress?> GetPartyQuestAsync(string groupId);
Task SavePartyQuestAsync(PartyQuestProgress progress);
```

Serialized as JSON alongside existing player state files.

---

## 11. Testing Strategy

| Layer | Test Type | What |
|---|---|---|
| `QuestEngine` | Unit (xUnit + Moq) | Accept/complete/fail/abandon lifecycle, prerequisite checking, reward distribution |
| `QuestTracker` | Unit | Objective progress updates for each `ObjectiveType`, stage advancement logic |
| `QuestSeedLoader` | Unit | YAML parsing, validation of required fields, registry population |
| `GameEngine` integration | Integration | End-to-end: enter room → talk to NPC → accept quest → kill enemies → complete quest → verify rewards |
| `PartyQuestProgress` | Unit | Multi-player contribution pooling, reward distribution to all members |
| Narrator prompts | Manual / Playwright | Verify quest context appears in narrator prompts at correct moments |

---

## 12. Implementation Phases

### Phase 1: Foundation (Est. 3-5 days)

- [ ] `QuestDefinition`, `QuestStage`, `QuestObjective`, `QuestReward` models
- [ ] `QuestProgress`, `ObjectiveProgress`, `QuestStatus` models
- [ ] `IContentRegistry<QuestDefinition>` registration
- [ ] `QuestSeedLoader` — YAML parsing and registry loading
- [ ] `IStateManager` extensions for quest persistence
- [ ] Unit tests for models and seed loading

### Phase 2: Core Engine (Est. 5-8 days)

- [ ] `QuestEngine` — accept, advance, complete, fail, abandon logic
- [ ] `QuestTracker` — objective evaluation and progress tracking
- [ ] `QuestTracker` hooks in `GameEngine` (combat, inventory, room entry, conversation)
- [ ] `PlayerCharacter.QuestLog` integration
- [ ] `Npc.QuestsOffered` field and prerequisite checking
- [ ] `CommandParser` additions (quest, journal, accept, abandon)
- [ ] Unit tests for quest lifecycle and tracker hooks

### Phase 3: Narrator Integration (Est. 3-5 days)

- [ ] Quest prompt templates (`QuestPrompts.cs`)
- [ ] Prompt injection at offer/progress/completion/failure moments
- [ ] Narrator response schema extension (`quest_updates` field)
- [ ] Natural language quest acceptance detection
- [ ] Manual testing of narrator quest delivery quality

### Phase 4: Party Quests (Est. 3-4 days)

- [ ] `PartyQuestProgress` model and state management
- [ ] Multi-player objective pooling logic
- [ ] Party reward distribution
- [ ] Party quest join/leave flow
- [ ] Unit tests for party mechanics

### Phase 5: Content and Polish (Est. 2-3 days)

- [ ] Author 5-8 starter quests in `config/quests.yaml` covering all objective types
- [ ] Add `quests_offered` to existing NPCs in `lore-seed.yaml`
- [ ] Integration testing with narrator
- [ ] Quest journal formatting and display polish

**Total estimated effort: 16-25 days**

---

## 13. Resolved Design Decisions

These were reviewed and decided by the project owner on 2026-04-05:

1. **Custom objectives and AI judgment:** Custom objective conditions are included in every narrator turn while the quest is active. The AI evaluates and **recommends** completion, but the **engine makes the final call** — the narrator's `quest_updates.custom_objective_met` is a recommendation flag that the `QuestEngine` validates before applying. Cache the evaluation: if the game state hasn't changed since the last check, skip the re-evaluation.

2. **Quest failure triggers:** The narrator can **recommend** quest failure (e.g., player insults the quest giver, destroys a key item), but the engine validates against explicit failure conditions before applying. The narrator flags `quest_updates.failure_recommended: { "quest_id": "reason" }` and the engine decides. This preserves the organic feel without risking hallucinated failures.

3. **Disposition gating:** **Yes.** Add a `MinDisposition` field to `QuestDefinition`. NPCs will only offer quests to players whose disposition meets the threshold. This creates gameplay incentive to build NPC relationships. Default value: `null` (no requirement). Add to YAML schema as `min_disposition: 30`.

4. **Database migration:** The Quest Engine ships **after** the PostgreSQL migration (see DATABASE-MIGRATION-SCOPE.md). Quest state will persist via EF Core from day one. The `IStateManager` quest methods defined in Section 10 will be implemented directly in `EfCoreStateManager`, not in the file-based system.

5. **Quest item inventory behavior:** Quest items (`ItemType.QuestItem`) are **locked** — non-droppable, non-sellable, zero weight. Add enforcement in the `Drop` and `Sell` action handlers in `GameEngine.cs`. If a player attempts to drop/sell a quest item, the narrator responds in-character: "Something tells you this is important. You'd better hold onto it."

---

*"A quest, sir, is merely a problem dressed in its finest clothes and sent out to find a hero. The engine that manages this wardrobe? That is what we build here."*
*— Sir Thaddeus, on the occasion of scoping things properly*
