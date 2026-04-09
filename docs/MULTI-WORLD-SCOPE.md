# Multi-World System — Project Scope Document

**Project:** Grand Adventure Engine (GAE)
**Author:** Mark Hall / Sir Thaddeus
**Date:** 2026-04-05
**Status:** Draft — Ready for GHC Review
**Depends on:** Database Migration (see DATABASE-MIGRATION-SCOPE.md)
**Related:** Quest Engine (see QUEST-ENGINE-SCOPE.md), Lore Book (future)

---

## 1. Executive Summary

The Multi-World System transforms the Grand Adventure Engine from a single-world RPG into a **library of worlds** that an admin can create, configure, and switch between. Each world defines its own rooms, NPCs, quests, lore, game rules, and even its own **stat tree** — a world might use classic STR/DEX/CON or something entirely custom like Might/Finesse/Fortitude.

**Characters persist across worlds.** When a player travels to a different realm, they keep their items, level, gold, and identity. The AI narrator **translates their stats** to the destination world's system, improvising mappings where no clean equivalent exists. When they return, items gained in other worlds stay with them and their stats are re-translated back.

**Entities can be shared.** NPCs, items, and other content exist as shared references tagged to one or more worlds. An NPC in multiple worlds is the same canonical record, not a copy. However, **knowledge does not transfer** — an NPC in World A doesn't know what happened in World B.

**Quests are world-scoped.** Quest definitions and progress are bound to the world they belong to.

**The admin UI provides world management** — select a world, and rooms/NPCs/items/quests filter to that world. Switch worlds without confusion.

---

## 2. Goals and Non-Goals

### Goals

- A `World` entity that defines: name, description, game rules (including custom stat tree), spawn room, and content tags
- World-specific `game-rules.yaml` equivalent stored per-world (stat definitions, combat formulas, skill checks, etc.)
- Admin CRUD for worlds (create, edit, delete, activate/deactivate)
- Admin world-switching: select a world → all dashboard content filters to that world
- World tagging on rooms, NPCs, items, and quests — each entity lists which worlds it belongs to
- Shared entity references across worlds (one NPC record, tagged to multiple worlds)
- Per-world knowledge isolation: NPC dialogue context, story entries, and narrator knowledge are world-scoped
- Player realm travel via admin control AND in-world portals
- AI-mediated stat translation when characters cross between worlds with different stat systems
- Persistent character identity: items, gold, level, XP travel with the player; nothing is lost on realm transfer
- Per-world spawn point: characters arriving in a new world land at that world's designated spawn room
- Quest progress is world-scoped — leaving a world doesn't advance or lose quest progress, it's frozen until return
- Future-proof for Lore Book: world tagging on lore entries (planned, not implemented here)

### Non-Goals (out of scope)

- Lore Book implementation (explicitly deferred — next phase)
- Per-world visual themes or UI customization (the CRT terminal stays consistent)
- World templates or cloning (admin creates worlds from scratch for now)
- Cross-world chat or player communication
- Automated world generation (AI creates entire worlds — future)
- Per-world player accounts (one player, many worlds — not one player per world)

---

## 3. Core Concepts

### 3.1 The World Entity

A world is the top-level container. It defines the rules of reality for everything inside it.

```
World
├── Game Rules (stat tree, combat formulas, skill checks, rest, death, leveling)
├── Rooms (tagged to this world)
│   ├── NPCs (tagged; shared references, but knowledge is world-scoped)
│   └── Items (tagged; shared references)
├── Quests (world-scoped)
├── Story Entries (world-scoped)
├── Combat States (world-scoped)
├── Portals (connections to other worlds)
└── Spawn Point (where incoming travelers arrive)
```

### 3.2 Entity World Membership

Every room, NPC, item template, and quest carries a `WorldIds` list — the worlds it appears in. This is a **tag, not a copy**. Editing an NPC's personality updates it everywhere it appears.

What IS world-scoped (separate per world):
- Story entries (what happened in this world)
- NPC knowledge context (what an NPC knows in this world)
- NPC disposition state (how an NPC feels about a player in this world)
- Quest definitions and progress
- Combat states
- Room instance state (per-player room clones are world-specific)

What is NOT world-scoped (shared across worlds):
- NPC identity (name, personality template, base stats)
- Item definitions (name, stats, type, value)
- Player character (identity, inventory, level, gold)

### 3.3 Stat Translation — The Soul System

This is the most novel part of the architecture. When a player moves between worlds with different stat trees, the AI narrator performs a **stat translation**. We call this the "Soul System" because the character's essence persists — only the expression changes.

**How it works:**

1. **Departure:** The engine snapshots the character's current stats in the origin world's format and stores them as `WorldStatSnapshot` (frozen state for when they return)
2. **Translation Request:** The engine sends the AI a structured prompt containing:
   - The character's stats, class, race, backstory, and playstyle
   - The origin world's stat definitions (names, ranges, categories)
   - The destination world's stat definitions
   - Any previous translations for this character (for consistency)
3. **AI Response:** The narrator returns a structured JSON mapping:
   ```json
   {
     "translated_stats": { "Might": 14, "Finesse": 12, "Fortitude": 16 },
     "translation_notes": "STR→Might (direct), DEX→Finesse (direct), CON→Fortitude (direct). INT/WIS/CHA have no equivalent in this world's martial system — folded into a general 'Resolve' stat.",
     "narrative": "As you step through the portal, your body shifts and reshapes. The familiar weight of your strength becomes something rawer — a burning Might that settles into your bones..."
   }
   ```
4. **Application:** The engine applies the translated stats and the narrator delivers the flavor text
5. **Return:** When the player returns to the origin world, their `WorldStatSnapshot` is restored. Any new items, gold, XP, and level-ups persist. The AI narrates the re-adjustment.

**Edge cases the AI handles:**
- Destination world has fewer stats → AI consolidates (e.g., STR + CON → Might)
- Destination world has more stats → AI distributes (e.g., DEX → Agility + Precision)
- Stats have different scales → AI normalizes (e.g., 1-20 in origin, 1-100 in destination)
- Class doesn't exist in destination world → AI finds closest equivalent, narrates the shift
- No clean mapping exists → AI improvises and explains the translation narratively

**Consistency guarantee:** Translation history is stored per-character-per-world-pair. If a player bounces between the same two worlds repeatedly, the AI references prior translations for consistency. Cached translations are reused when nothing has changed; re-translation is triggered when stats change, the player levels up, or the world's stat tree is edited.

### 3.4 Knowledge Isolation

When an NPC exists in multiple worlds, their personality and identity are shared but their **knowledge and memory are not**. Mechanically:

- `NpcDispositionState` (emotion, intensity, memory flags) is stored per `(npc_id, world_id, player_id)` — not just per NPC
- Story entries carry a `world_id` — when building narrator context, only entries from the current world are included
- The lore/knowledge pipeline filters by world tags
- Future Lore Book entries will be tagged with world IDs and only injected into narrator prompts for matching worlds

---

## 4. Data Models

### 4.1 World

```csharp
public class World
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SpawnRoomId { get; set; } = "spawn";
    public bool IsActive { get; set; } = true;

    // The full game rules for this world (stat definitions, combat, skill checks, etc.)
    // Stored as JSONB — equivalent to the current game-rules.yaml but per-world
    public GameRulesConfig Rules { get; set; } = new();

    // Admin metadata
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Tags for categorization ("fantasy", "sci-fi", "horror", "comedy")
    public List<string> Tags { get; set; } = [];

    // Portal configuration — which other worlds can be reached from this one
    public List<WorldPortal> Portals { get; set; } = [];
}
```

### 4.2 WorldPortal

```csharp
public class WorldPortal
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SourceWorldId { get; set; } = string.Empty;
    public string SourceRoomId { get; set; } = string.Empty;  // Room where the portal exists
    public string DestinationWorldId { get; set; } = string.Empty;
    public string? DestinationRoomId { get; set; }  // null = use destination world's spawn

    // Narrator flavor
    public string? Description { get; set; }  // "A shimmering rift in the air..."
    public string? NarratorHint { get; set; }  // Tone guidance for the transition narration

    // Access control
    public bool IsAdminOnly { get; set; } = false;
    public int? MinLevel { get; set; }
    public List<string> RequiredCompletedQuests { get; set; } = [];
}
```

### 4.3 WorldStatSnapshot (frozen stats for return trips)

```csharp
public class WorldStatSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;

    // Frozen stats in this world's format
    public Dictionary<string, int> Stats { get; set; } = new();

    // Character metadata at time of departure
    public string? Class { get; set; }
    public string? Race { get; set; }
    public int Level { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Mp { get; set; }
    public int MaxMp { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 4.4 StatTranslationHistory (AI consistency memory)

```csharp
public class StatTranslationHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PlayerId { get; set; } = string.Empty;
    public string SourceWorldId { get; set; } = string.Empty;
    public string DestinationWorldId { get; set; } = string.Empty;

    // What the AI decided last time
    public Dictionary<string, int> TranslatedStats { get; set; } = new();
    public string TranslationNotes { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 4.5 WorldNpcState (per-world NPC disposition and memory)

```csharp
public class WorldNpcState
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string NpcId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string? PlayerId { get; set; }  // null = global disposition in this world

    // World-specific disposition (same structure as current NpcDispositionState)
    public NpcDispositionState DispositionState { get; set; } = new();

    // World-specific knowledge overrides
    public List<string>? KnowledgeScopeOverrides { get; set; }
}
```

### 4.6 PlayerWorldState (per-player, per-world metadata)

```csharp
public class PlayerWorldState
{
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string CurrentRoomId { get; set; } = string.Empty;  // Where they are in THIS world
    public bool HasVisited { get; set; } = false;
    public DateTimeOffset? LastVisitedAt { get; set; }
    public DateTimeOffset? FirstVisitedAt { get; set; }
}
```

---

## 5. Database Schema Additions

These tables build on top of the schema defined in DATABASE-MIGRATION-SCOPE.md.

### 5.1 New Tables

#### `worlds`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | |
| `name` | `text NOT NULL` | |
| `description` | `text` | |
| `spawn_room_id` | `text NOT NULL` | |
| `is_active` | `boolean DEFAULT true` | |
| `rules` | `jsonb NOT NULL` | Full `GameRulesConfig` for this world |
| `tags` | `jsonb` | `List<string>` |
| `portals` | `jsonb` | `List<WorldPortal>` |
| `created_by` | `text` | |
| `created_at` | `timestamptz` | |
| `updated_at` | `timestamptz` | |

#### `world_stat_snapshots`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | |
| `player_id` | `text NOT NULL` | FK → players |
| `world_id` | `text NOT NULL` | FK → worlds |
| `stats` | `jsonb NOT NULL` | Frozen stat values |
| `class` | `text` | |
| `race` | `text` | |
| `level` | `integer` | |
| `hp` | `integer` | |
| `max_hp` | `integer` | |
| `mp` | `integer` | |
| `max_mp` | `integer` | |
| `created_at` | `timestamptz` | |

**Unique constraint:** `(player_id, world_id)` — one snapshot per player per world

#### `stat_translation_history`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | |
| `player_id` | `text NOT NULL` | FK → players |
| `source_world_id` | `text NOT NULL` | FK → worlds |
| `destination_world_id` | `text NOT NULL` | FK → worlds |
| `translated_stats` | `jsonb NOT NULL` | |
| `translation_notes` | `text` | |
| `created_at` | `timestamptz` | |

**Index:** `(player_id, source_world_id, destination_world_id)` — for consistency lookups

#### `world_npc_states`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | |
| `npc_id` | `text NOT NULL` | |
| `world_id` | `text NOT NULL` | FK → worlds |
| `player_id` | `text` | nullable (global vs per-player) |
| `disposition_state` | `jsonb NOT NULL` | |
| `knowledge_scope_overrides` | `jsonb` | |

**Unique constraint:** `(npc_id, world_id, player_id)` — one state per combo

#### `player_world_states`

| Column | Type | Notes |
|---|---|---|
| `player_id` | `text NOT NULL` | FK → players |
| `world_id` | `text NOT NULL` | FK → worlds |
| `current_room_id` | `text NOT NULL` | |
| `has_visited` | `boolean DEFAULT false` | |
| `first_visited_at` | `timestamptz` | |
| `last_visited_at` | `timestamptz` | |

**PK:** `(player_id, world_id)`

### 5.2 Modified Tables (from Database Migration scope)

#### `players` — additions

| Column | Type | Notes |
|---|---|---|
| `active_world_id` | `text` | FK → worlds. Which world they're currently in |
| `home_world_id` | `text` | FK → worlds. Their "native" world (where stats are canonical) |

#### `rooms` — additions

| Column | Type | Notes |
|---|---|---|
| `world_ids` | `jsonb` | `List<string>` — which worlds this room appears in |

#### `player_rooms` — additions

| Column | Type | Notes |
|---|---|---|
| `world_id` | `text NOT NULL` | FK → worlds. Room instances are world-scoped |

**Modified unique constraint:** `(player_id, room_id, world_id)`

#### `story_entries` — additions

| Column | Type | Notes |
|---|---|---|
| `world_id` | `text NOT NULL` | FK → worlds. Stories are world-scoped |

**New index:** `ix_story_entries_world_id`

#### `combat_states` — additions

| Column | Type | Notes |
|---|---|---|
| `world_id` | `text NOT NULL` | FK → worlds |

**Modified PK:** `(room_id, world_id)` — combat is per-room-per-world

#### `npcs` (if NPC records move to DB, or via the registry)

NPCs remain in the content registry (YAML-seeded, in-memory) per the DB migration scope. Their world membership is tracked via a `world_ids` tag in the YAML definition. World-specific state (disposition, knowledge) lives in `world_npc_states`.

---

## 6. GameRulesConfig — Per-World Rules

The existing `game-rules.yaml` becomes a **per-world configuration** stored as JSONB in the `worlds.rules` column. The current world's rules serve as the default for new worlds.

**Note:** `GameRulesConfig` already exists at `GAE.Engine/Configuration/GameRulesConfig.cs` with the exact structure needed: `CharacterCreationConfig`, `StatConfig`, `CombatConfig`, `SkillCheckConfig`, `RestConfig`, `DeathConfig`, `LootConfig`, `LevelingConfig`. The only modification needed is adding `SemanticTags` to `StatConfig`:

```csharp
// Existing class in GAE.Engine/Configuration/GameRulesConfig.cs
public class StatConfig
{
    public int Base { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public string Display { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    // NEW: Semantic tags for AI stat translation between worlds
    // e.g., ["physical_power", "melee", "carry_capacity"]
    public List<string> SemanticTags { get; set; } = [];
}
```

**The `SemanticTags` field is key.** It gives the AI translation system structured hints:

```yaml
# World A (classic D&D)
stats:
  str:
    display: "STR"
    semantic_tags: ["physical_power", "melee", "carry_capacity"]
  dex:
    display: "DEX"
    semantic_tags: ["agility", "ranged", "reflexes", "stealth"]

# World B (martial arts world)
stats:
  might:
    display: "Might"
    semantic_tags: ["physical_power", "melee", "carry_capacity", "intimidation"]
  flow:
    display: "Flow"
    semantic_tags: ["agility", "ranged", "reflexes", "evasion", "grace"]
```

The AI sees the overlap in semantic tags and knows STR→Might and DEX→Flow are reasonable mappings. Where tags don't overlap, it improvises and explains why.

---

## 7. Realm Travel Flow

### 7.1 Player-Initiated (Portal)

```
1. Player enters room containing a portal → narrator describes it
2. Player interacts: "step through the portal" / "enter the rift"
3. Engine validates access (level, quest requirements)
4. SNAPSHOT: Save current stats as WorldStatSnapshot for current world
5. SAVE POSITION: Update PlayerWorldState with current room in current world
6. TRANSLATE: Call AI with stat translation prompt (include translation history for consistency)
7. APPLY: Set player's stats to translated values, set active_world_id to destination
8. POSITION: Set player's current_room_id to destination world's spawn (or portal target)
9. LOAD: Load destination world's rules, rooms, NPCs into the engine context
10. NARRATE: AI delivers the transition narrative with stat translation flavor
11. Player is now in the new world
```

### 7.2 Admin-Initiated (Teleport)

```
1. Admin selects player + destination world via dashboard
2. Same steps 4-11 as above, but no portal validation
3. Admin can optionally override the destination room (not just spawn)
4. Narrator still delivers transition flavor (admin teleports aren't jarring breaks)
```

### 7.3 Return to Previous World

```
1. Player travels back to a world they've visited before
2. Engine finds their WorldStatSnapshot for that world
3. CHECK: Have the player's stats changed since departure? (level up, stat gains, etc.)
   - If NO changes: RESTORE snapshot directly (fast path, no AI call)
   - If YES changes: RE-TRANSLATE — call AI with updated stats, origin world context,
     and the previous translation for consistency. Update both the snapshot and
     translation history.
4. ADJUST: Level and XP always carry over — AI adjusts HP/MP scaling based on new level + world rules
5. ITEMS: All items gained in other worlds persist in inventory
6. POSITION: Player lands at their last known room in that world (from PlayerWorldState), OR spawn if first visit
7. QUEST STATE: Quest progress for this world is exactly where they left it
8. NARRATE: AI describes the homecoming, noting any new items, stat changes, or level ups
```

**Why re-translate on stat gain:** If a player levels up or gains stats in World B, their World A snapshot is now stale — their character is more powerful than when they left. The engine detects this by comparing the player's current level/stats against the snapshot's stored values. If anything changed, the AI re-translates to keep the character's power level consistent across worlds.

---

## 8. Admin UI — World Management

### 8.1 Admin Dashboard Flow

```
┌─────────────────────────────────────────────────────┐
│  ADMIN DASHBOARD                                     │
│  ┌──────────────────────────────────────────────┐    │
│  │  World Selector  [▼ Rabanastre (Active)    ] │    │
│  │                  [ Cyberpunk 2099           ] │    │
│  │                  [ The Hollow Realm         ] │    │
│  │                  [+ Create New World        ] │    │
│  └──────────────────────────────────────────────┘    │
│                                                       │
│  ┌─ World: Rabanastre ──────────────────────────┐    │
│  │  Rooms: 12  │  NPCs: 8  │  Quests: 3        │    │
│  │  Players currently here: 4                    │    │
│  │  [Edit World] [Rules] [Portals] [Seed Data]  │    │
│  └──────────────────────────────────────────────┘    │
│                                                       │
│  ┌─ Content (filtered to Rabanastre) ───────────┐    │
│  │  [Rooms] [NPCs] [Items] [Quests] [Players]   │    │
│  │  ┌──────────────────────────────────────────┐ │    │
│  │  │ The Seventh Heaven  │ spawn  │ 2 NPCs   │ │    │
│  │  │ Town Square         │ town.. │ 3 NPCs   │ │    │
│  │  │ Back Alley          │ back.. │ 1 NPC    │ │    │
│  │  └──────────────────────────────────────────┘ │    │
│  └──────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────┘
```

### 8.2 Key Admin UI Principles

1. **World selector is always visible** — a dropdown/pill at the top of every admin page. You always know which world you're looking at.
2. **Content filters automatically** — when "Rabanastre" is selected, only rooms/NPCs/items/quests tagged to Rabanastre appear in lists.
3. **Cross-world entities are clearly marked** — if an NPC appears in 3 worlds, show a badge: "🌍 3 worlds". Editing it shows a warning: "This entity exists in 3 worlds. Changes apply everywhere."
4. **Players show their current world** — the player list shows where each player currently is. Filtering by world shows only players in that world.
5. **World rules editor** — a dedicated panel for editing a world's stat tree, combat formulas, etc. Shows a live diff against the "default" rules.
6. **Portal manager** — visual editor for linking worlds via portals. Shows which rooms have portals and where they lead.

### 8.3 New Admin API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| GET | `/admin/worlds` | List all worlds |
| GET | `/admin/worlds/{worldId}` | Get world details + rules |
| POST | `/admin/worlds` | Create new world |
| PUT | `/admin/worlds/{worldId}` | Update world (name, description, rules) |
| DELETE | `/admin/worlds/{worldId}` | Delete world (must have no active players) |
| POST | `/admin/worlds/{worldId}/activate` | Set world as active |
| POST | `/admin/worlds/{worldId}/deactivate` | Deactivate world |
| GET | `/admin/worlds/{worldId}/players` | List players in this world |
| POST | `/admin/worlds/{worldId}/portals` | Create portal link |
| DELETE | `/admin/worlds/{worldId}/portals/{portalId}` | Remove portal |
| POST | `/admin/mutations/realm-transfer` | Move player to different world |
| GET | `/admin/worlds/{worldId}/content` | Get all content for a world (rooms, NPCs, items, quests) |
| POST | `/admin/worlds/{worldId}/seed` | Seed a world from YAML file |

### 8.4 Modified Existing Endpoints

All existing content endpoints gain an optional `?worldId=` query parameter:

- `GET /dashboard/rooms?worldId=xxx` → only rooms in that world
- `GET /dashboard/admin/registry/items?worldId=xxx` → only items tagged to that world
- `GET /dashboard/story?worldId=xxx` → only story entries from that world

When `worldId` is omitted, behavior depends on context:
- Admin endpoints: return content for the admin's currently selected world (stored in session/cookie)
- Player endpoints: return content for the player's `active_world_id`

---

## 9. World Seeding and Content Management

### 9.1 Default World Migration

The existing single world becomes "World 1" — the default world. On first boot after migration:

1. Create a `World` record with the current `game-rules.yaml` as its rules
2. Tag all existing rooms, NPCs, items, and quests with this world's ID
3. Set all existing players' `active_world_id` and `home_world_id` to this world
4. Set all existing story entries' `world_id` to this world

### 9.2 Per-World YAML Seeding

New worlds can be seeded from YAML files similar to `lore-seed.yaml`, but with a world wrapper:

```yaml
world:
  id: "cyberpunk_2099"
  name: "Cyberpunk 2099"
  description: "A neon-drenched dystopia where megacorps rule and chrome is life."
  spawn_room_id: "neon_bar"
  tags: ["sci-fi", "cyberpunk", "dystopia"]

rules:
  stats:
    reflex:
      display: "REF"
      base: 10
      min: 1
      max: 20
      category: "attribute"
      semantic_tags: ["agility", "reflexes", "ranged"]
    body:
      display: "BODY"
      base: 10
      min: 1
      max: 20
      category: "attribute"
      semantic_tags: ["physical_power", "melee", "carry_capacity", "toughness"]
    tech:
      display: "TECH"
      base: 10
      min: 1
      max: 20
      category: "attribute"
      semantic_tags: ["crafting", "hacking", "engineering", "intelligence"]
    cool:
      display: "COOL"
      base: 10
      min: 1
      max: 20
      category: "attribute"
      semantic_tags: ["charisma", "intimidation", "composure", "social"]
    # ... combat, skill checks, etc.

rooms:
  - id: "neon_bar"
    name: "The Flickering Pixel"
    description: "Bass thumps through chrome walls..."
    # ...

npcs:
  - id: "bartender_chrome"
    name: "Chrome Jackie"
    # ...
```

### 9.3 World Rules Editor

The admin dashboard provides a form-based editor for world rules, organized by section:

- **Stat Tree** — add/remove/rename stats, set ranges, assign semantic tags
- **Character Creation** — starting stats, HP/MP formulas, starting items
- **Combat** — attack/damage formulas, critical thresholds, defense calculation
- **Skill Checks** — difficulty classes, stat mappings
- **Rest/Death/Leveling** — recovery formulas, death mechanics, XP scaling

Changes are validated before saving (e.g., formulas must reference valid stat names for this world).

---

## 10. AI Narrator Integration

### 10.1 Stat Translation Prompt

```
SYSTEM: You are the Grand Adventure Engine's stat translator. A character is
crossing between worlds with different stat systems. Your job is to translate
their stats as faithfully as possible, using the semantic tags as guides.

CHARACTER:
- Name: {name}  Class: {class}  Race: {race}  Level: {level}
- Origin World: {source_world_name}
- Origin Stats: {source_stats_with_values_and_semantic_tags}

DESTINATION WORLD: {destination_world_name}
DESTINATION STATS: {destination_stat_definitions_with_semantic_tags}

PREVIOUS TRANSLATIONS (for consistency):
{translation_history_if_any}

RULES:
1. Map stats by semantic tag overlap first. Direct overlaps are 1:1 mappings.
2. If origin has stats with no destination equivalent, fold their value into
   the closest related stat. Explain why.
3. If destination has stats with no origin equivalent, derive from the closest
   related origin stats. Explain why.
4. Preserve the character's relative power level. A strong character stays strong.
5. Respect destination stat ranges (min/max). Scale proportionally if needed.
6. If previous translations exist for this world pair, stay consistent unless
   the character's level has changed significantly.
7. Return ONLY valid JSON in the specified format.

RESPOND WITH:
{
  "translated_stats": { "<stat_id>": <value>, ... },
  "translation_notes": "<explain your reasoning>",
  "narrative": "<2-3 sentences describing the physical sensation of the transformation>"
}
```

### 10.2 World Context Injection

Every narrator prompt gains a world context header:

```
CURRENT WORLD: {world_name}
WORLD DESCRIPTION: {world_description}
WORLD RULES: {summarized_rules — stat names, combat style, tone}
```

This ensures the narrator writes in the appropriate tone and references the correct stat names.

### 10.3 Knowledge Isolation in Prompts

When building NPC conversation context:
- Only include story entries where `world_id == current_world_id`
- Only include NPC disposition from `world_npc_states` for current world
- Only include lore knowledge tagged to current world
- NPCs do NOT know about events in other worlds unless explicitly tagged

---

## 11. IStateManager Interface Changes

The multi-world system requires extending `IStateManager`. These are **additive** — no existing methods change signature.

```csharp
// New methods on IStateManager (or a new IWorldStateManager interface)

// World CRUD
Task<World?> GetWorldAsync(string worldId, CancellationToken ct = default);
Task<IReadOnlyList<World>> GetAllWorldsAsync(CancellationToken ct = default);
Task SaveWorldAsync(World world, CancellationToken ct = default);
Task<bool> RemoveWorldAsync(string worldId, CancellationToken ct = default);

// Player world state
Task<PlayerWorldState?> GetPlayerWorldStateAsync(string playerId, string worldId, CancellationToken ct = default);
Task SavePlayerWorldStateAsync(PlayerWorldState state, CancellationToken ct = default);

// Stat snapshots
Task<WorldStatSnapshot?> GetStatSnapshotAsync(string playerId, string worldId, CancellationToken ct = default);
Task SaveStatSnapshotAsync(WorldStatSnapshot snapshot, CancellationToken ct = default);

// Translation history
Task<StatTranslationHistory?> GetTranslationHistoryAsync(string playerId, string sourceWorldId, string destWorldId, CancellationToken ct = default);
Task SaveTranslationHistoryAsync(StatTranslationHistory history, CancellationToken ct = default);

// World-scoped NPC state
Task<WorldNpcState?> GetWorldNpcStateAsync(string npcId, string worldId, string? playerId, CancellationToken ct = default);
Task SaveWorldNpcStateAsync(WorldNpcState state, CancellationToken ct = default);

// Modified: existing methods that now need world context
// Option A: Add worldId parameter overloads
// Option B: Inject world context via a scoped "current world" service
// Recommendation: Option B — cleaner, avoids changing 20+ method signatures
```

### 11.1 Scoped World Context (Recommended Approach)

Instead of adding `worldId` to every method, introduce a scoped service:

```csharp
public interface IWorldContext
{
    string CurrentWorldId { get; }
    World CurrentWorld { get; }
    GameRulesConfig Rules { get; }
}

public class WorldContext : IWorldContext
{
    // Set per-request based on:
    // - Player's active_world_id (for player endpoints)
    // - Admin's selected world (for admin endpoints, via cookie/header)
}
```

`EfCoreStateManager` injects `IWorldContext` and automatically filters queries:
- `GetAllRoomsAsync()` → returns only rooms where `world_ids` contains current world
- `GetStoryEntriesAsync()` → filters by `world_id == current`
- `GetCombatStateAsync()` → filters by `world_id == current`

This keeps the `IStateManager` interface **unchanged** for callers while silently scoping all queries.

---

## 12. Testing Strategy

| Layer | Test Type | What |
|---|---|---|
| World CRUD | Unit | Create, read, update, delete worlds |
| WorldContext | Unit | Scoped filtering of rooms/NPCs/story by world |
| Stat Translation | Unit + Manual | Translation prompt generation, response parsing, snapshot save/restore |
| Realm Travel | Integration | Full flow: snapshot → translate → apply → verify player state in new world |
| Return Trip | Integration | Travel to World B → gain items/XP → return to World A → verify snapshot restore + items kept |
| Knowledge Isolation | Integration | NPC in World A doesn't reference World B story entries |
| Admin World Switching | Integration | Select world → verify content filtering across all endpoints |
| Portal System | Integration | Player enters portal room → triggers realm transfer → lands at destination spawn |
| Default Migration | Integration | Boot with existing data → verify single world created with all content tagged |

---

## 13. Implementation Phases

### Phase 1: World Entity and Schema (Est. 3-4 days)

- [ ] `World` model and EF Core configuration
- [ ] `GameRulesConfig` as structured JSONB model (refactor from current YAML loader)
- [ ] `PlayerWorldState`, `WorldStatSnapshot`, `StatTranslationHistory` models
- [ ] `WorldNpcState` model
- [ ] EF Core migration adding all new tables
- [ ] Modify existing tables (add `world_id` columns, adjust constraints)
- [ ] Default world migration: existing content → tagged to default world
- [ ] Unit tests for all new models

### Phase 2: World Context and Query Scoping (Est. 4-5 days)

- [ ] `IWorldContext` / `WorldContext` scoped service
- [ ] Middleware to set current world from player or admin session
- [ ] Modify `EfCoreStateManager` to filter by `IWorldContext`
- [ ] Update `PlayerCharacter` with `active_world_id` and `home_world_id`
- [ ] Update story entries, combat states, player rooms to carry `world_id`
- [ ] Verify existing game flow works unchanged (single world, default context)
- [ ] Integration tests for world-scoped queries

### Phase 3: Realm Travel and Stat Translation (Est. 5-7 days)

- [ ] `StatDefinition` with semantic tags
- [ ] Stat translation prompt template
- [ ] AI response parsing and validation
- [ ] `WorldStatSnapshot` save/restore logic
- [ ] `StatTranslationHistory` consistency lookup
- [ ] Realm travel flow: snapshot → translate → apply → position → narrate
- [ ] Return trip flow: restore snapshot → adjust for level/XP changes
- [ ] `WorldPortal` model and in-room portal detection
- [ ] Admin teleport-to-world mutation
- [ ] Integration tests for realm travel round-trips

### Phase 4: Admin UI — World Management (Est. 5-7 days)

- [ ] World CRUD API endpoints
- [ ] World selector dropdown (frontend)
- [ ] Content filtering by selected world (rooms, NPCs, items, quests lists)
- [ ] World rules editor (stat tree, combat, skill checks)
- [ ] Portal manager (create/edit/delete portals between worlds)
- [ ] Player world status display (which world, last visited)
- [ ] Realm transfer admin action (move player between worlds)
- [ ] Cross-world entity badges ("🌍 3 worlds" indicator)

### Phase 5: Knowledge Isolation and Polish (Est. 3-4 days)

- [ ] `WorldNpcState` integration — disposition per world per player
- [ ] Narrator prompt context filtering by world
- [ ] Story entry world-scoping in narrator context builder
- [ ] Per-world YAML seeding tool (load a world from a YAML file)
- [ ] Default world migration verification (existing data still works)
- [ ] End-to-end testing: multi-world gameplay session
- [ ] Update AGENTS.md and HANDOFF.md

**Total estimated effort: 20-27 days**

---

## 14. Resolved Design Decisions

These were reviewed and decided by the project owner on 2026-04-05:

1. **Stat semantic tags:** **Predefined vocabulary** of ~30 curated tags (`physical_power`, `agility`, `arcane`, `social`, `stealth`, `toughness`, etc.) with autocomplete in the admin UI. No freeform text — consistent tags mean consistent AI translation behavior. The vocabulary is defined as a static list in the codebase, not in the database.

2. **Item compatibility across worlds:** **AI re-flavors dynamically.** Items keep their mechanical stats (damage dice, armor value, bonuses) but the narrator re-describes them contextually. "Enchanted Longsword" in a fantasy world becomes "a blade humming with unfamiliar energy" in a sci-fi world. No per-world description storage needed — the narrator handles it via world context injection.

3. **Class translation:** **Class name persists, abilities adapt.** A "Paladin" entering a sci-fi world is still called a Paladin, but the narrator contextualizes their abilities within the destination world's framework. Class is identity. The AI stat translation prompt already handles the mechanical side.

4. **Max worlds / schema design:** **Under 10 worlds expected.** Entity-to-world membership uses a **JSONB array** (`world_ids`) on each entity. No join table needed at this scale. Simple, queryable via PostgreSQL JSONB operators (`@>`, `?`), and matches the under-10 world target.

5. **World deletion safety:** **Require evacuation.** Admin must transfer all players out of a world before deletion is allowed. The DELETE endpoint returns a `409 Conflict` with a list of players still in the world and their IDs. Dashboard shows a blocking indicator: "4 players still in this world — transfer them before deleting."

6. **Stat translation caching:** **Cache with smart invalidation.** Translations are cached per `(player_id, source_world_id, destination_world_id)` in the `stat_translation_history` table. Cache is invalidated and re-translation triggered when:
   - Player's **stats change** (level up, stat point allocation, any stat gain from any source)
   - Player gains a **level** (even if stats haven't explicitly changed, HP/MP scaling may differ)
   - The destination world's **stat tree is edited** by admin (rules change)
   - If none of the above have changed since the last translation, the cached translation is reused instantly with no AI call. This means normal portal usage is fast, and re-translation only happens when something actually changed.

---

*"Multiple worlds, sir, are not merely parallel stories — they are parallel rule sets wearing different costumes. The trick is not in moving the body between them, but in translating the soul. That, I am pleased to report, is what we have designed here."*
*— Sir Thaddeus, on dimensional logistics*
