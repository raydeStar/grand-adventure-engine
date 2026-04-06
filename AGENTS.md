# Grand Adventure Engine — Agent Context

> "Rules are the skeleton upon which the flesh of adventure hangs." — Sir Thaddeus

This file is the authoritative context document for any AI agent (coding or narrative) working in this codebase.
Read it before writing code, generating prompts, or modifying narrator behavior.

---

## What This Project Is

A text-based RPG engine that runs as a Discord bot and a web dashboard (ASP.NET Core + Vanilla JS).
Players type natural-language commands. The AI Game Master simulates consequences, voices NPCs, handles combat,
and narrates outcomes. It is NOT a chatbot. It is a simulation with real state.

**Core principle:** Every player input produces a consequence. Nothing is ignored. Nothing returns a canned fallback.

---

## Architecture Overview

```
GAE.Core/           — Models, interfaces (PlayerCharacter, Room, Npc, InteractionState, etc.)
GAE.Engine/         — Game logic: CommandParser, GameEngine, ProbabilityEngine
GAE.Narrator/       — AI narrator: NarratorService (LM Studio / OpenAI-compatible backend)
GAE.Dashboard.Api/  — ASP.NET API + SignalR hub + static React-lite frontend (wwwroot/)
GAE.Discord/        — Discord bot service
tests/              — Unit tests (GAE.Engine.Tests), integration tests, narrator tests
config/             — game-rules.yaml (stat definitions, combat formulas, loot tables)
```

**State is in-memory + journaled to disk.** No database. State lives in `InMemoryStateManager` + `JournaledStateManager`.
Rooms are generated on-demand by the narrator when a player moves to an undiscovered location.

---

## The Narrator Personality

The AI narrator is **Sir Thaddeus** — literary, dramatic, darkly witty, never breaking character.

- **Voice:** Second person, present tense. "You step into..." not "The hero stepped..."
- **Style:** Dense sensory detail. Earns every adjective. Dark humor welcome, melodrama is not.
- **Rules:**
  - ALWAYS address the player's character by name at least once per entry
  - NEVER ask the player questions
  - NEVER mention systems, dice rolls, prompts, or that narration is occurring
  - NEVER list room metadata (exits, NPC rosters, item lists) in narration
  - NEVER invent outcomes beyond what the mechanical result supplies
  - Shorter is usually better. 2-4 sentences. 5 max.

---

## Interaction State Machine

Players exist in one of these modes (stored in `PlayerCharacter.Interaction.Mode`):

| Mode         | Description                                              |
|--------------|----------------------------------------------------------|
| `Explore`    | Default. Moving, looking, picking things up.             |
| `Conversation` | Talking to an NPC. All input routes through the NPC.  |
| `Combat`     | Active fight. Turn-based until resolution.               |
| `Trading`    | Buying/selling. Economy logic applies.                   |
| `Stealth`    | Mid-stealth action. Awareness checks per turn.           |
| `Event`      | Scripted/AI-triggered scene.                             |

**Mode transitions are automatic** based on player actions and AI responses.
Walking away mid-conversation costs NPC disposition. Fleeing combat costs HP.

---

## NPC Disposition System

NPCs have a **dual-layer disposition system**:

**Flat string (backward compat):** `Npc.Disposition` — one of: `friendly | neutral | annoyed | angry | hostile | amused | flirtatious | flustered | scared | sad | suspicious | intrigued | grateful | disgusted | resigned | impressed | contemptuous`

**Rich state (preferred):** `Npc.DispositionState` — an `NpcDispositionState` object with:
- `Emotion` — current feeling (same values as above)
- `Intensity` — 0-100 scale. Anger at 20 = clipped tone. Anger at 80 = drawn steel.
- `Baseline` — long-term attitude: `friendly | neutral | wary | hostile`. The emotion drifts back toward this.
- `Reason` — why they feel this way: "Player kissed Mara without invitation."

`ApplyInteractionUpdate()` in GameEngine keeps both representations in sync.
The narrator prompt receives the full `DispositionState.ToString()` format.
Disposition is **persistent within the session** and shifts based on player behavior.
An NPC remembers being insulted, kissed, helped, or ignored.

---

## NPC Knowledge Scoping

NPCs have bounded knowledge via `Npc.KnowledgeScopes` — a list of string tags that control which wiki pages
the narrator can reference when voicing that NPC.

**Example:** Mara the Innkeeper has scopes `["ironhold", "thornveil", "shadow_market", "local"]`.
Ask her about Ironhold trade disputes? She knows. Ask about the Iron Guard's internal command structure? She shrugs.

Knowledge is assembled by `WorldKnowledgeBuilder.BuildScopedContextAsync()` which fetches:
1. Room page (common knowledge — everyone knows where they are)
2. NPC's own wiki page (backstory, personal history)
3. Pages matching their KnowledgeScopes (factions, regions)
4. Their own faction page

Dynamically generated NPCs get scopes inferred from their faction + room environment tags.

---

## Wiki Knowledge Pipeline

The wiki (Wiki.js via GraphQL) serves as the narrator's long-term memory:

```
lore-seed.yaml → wiki pages (synced at startup)
WorldKnowledgeBuilder → reads wiki → injects into narrator prompts
NarratorService → uses world knowledge for all narrator calls
```

**Key classes:**
- `WorldKnowledgeBuilder` — pre-fetches relevant wiki pages before narrator calls
  - `BuildContextAsync(room, player)` — generic, fetches room/NPC/faction/region pages
  - `BuildScopedContextAsync(room, npc, player)` — NPC-scoped, respects KnowledgeScopes
  - `SearchContextAsync(query)` — keyword search for free-form actions
- `IWikiService.SearchAsync()` / `GetPagesAsync()` — GraphQL read operations
- `IWikiService.CreateOrUpdatePageAsync()` — write operations

**All four narrator methods** (NarrateAction, ProcessFreeForm, ProcessConversation, ProcessCombat)
inject world knowledge into their user prompts. Conversation uses scoped knowledge; others use generic.

---

## Command Routing

```
Player input
  → CommandParser (regex matching)
  → If match: game engine handles it mechanically (move, attack, take, etc.)
  → If no match OR if player is in non-explore interaction mode:
      → NLP intent translation (narrator tries to map to canonical command)
      → If still no match: ProcessFreeFormActionAsync (AI Game Master)
  → Result always has narration. Zero canned fallbacks.
```

The `ProcessFreeFormActionAsync` path is THE SOUL OF THE GAME. It must never return generic text.

---

## Character Stats

Defined in `config/game-rules.yaml`. Categories:

- **Resources** (expendable bars): `hp`, `mp` — displayed always in stat bar
- **Attributes** (semi-permanent scores): `str`, `dex`, `con`, `int`, `wis`, `cha`, `luck` — on-demand via "stats"
- **Currencies**: `gold` — displayed in stat bar

Modifier formula: `Math.Floor((value - 10) / 2)` — standard D&D modifier.
Skill checks: `d20 + stat_mod vs DC` (trivial:5, easy:8, medium:12, hard:16, very_hard:20, legendary:25).

---

## Frontend (wwwroot/js/)

Vanilla JS, no framework. Key files:
- `app.js` — State management, event handlers, API calls, SignalR event routing
- `ui.js` — All DOM rendering (story log, room panel, stat bars, interaction chips)
- `api.js` — Fetch wrappers for the REST API
- `signalr-client.js` — SignalR hub connection and event dispatch
- `room-map.js` — ASCII room map renderer using rot.js

**Story log rules:**
- Only narration, player commands, and system messages (stat changes) appear in the log
- Room metadata (exits, NPC lists, item lists) is stripped by `_stripRoomMetadata()` before rendering
- Only the newest entry streams character-by-character. Older entries render instantly.
- The log is capped at 50 entries. Oldest entries are removed from the DOM automatically.
- `_lastStoryCount` and `_renderedActionIds` guard against duplicate renders from SignalR.

**Interaction mode UI:**
- `UI.updateInteractionMode(mode, target)` changes the input prompt and quick-command chips
- In `conversation` mode: prompt shows `[talking to <NPC>] >` and chips show conversation actions
- In `combat` mode: prompt shows `[COMBAT: <enemy>] >` and chips show attack/defend/flee

---

## Multi-World System

The engine supports multiple parallel worlds, each with its own rules, stats, rooms, and NPCs.

**Core architecture:**
```
World (id, name, rules, portals, tags)
  → GameRulesConfig (per-world stats, combat, leveling)
  → WorldPortal[] (connections between worlds)
  → WorldNpcState (per-world NPC disposition)
```

**Key classes:**
- `World` / `WorldPortal` — [src/GAE.Engine/Worlds/WorldModels.cs](src/GAE.Engine/Worlds/WorldModels.cs)
- `IWorldRepository` / `InMemoryWorldRepository` — CRUD for worlds, portals, NPC state
- `IWorldContext` / `WorldContextMiddleware` — scoped service resolving current world from `?worldId=`, `X-World-Id` header, or `player.ActiveWorldId`
- `RealmTravelService` — stat translation, snapshot save/restore, portal travel
- `WorldBootstrapService` — ensures default world exists, backfills existing content

**Player world tracking:**
- `PlayerCharacter.ActiveWorldId` — which world the player is currently in
- `PlayerCharacter.HomeWorldId` — the player's origin world (for stat restoration)
- `WorldStatSnapshot` — frozen copy of player stats when leaving a world
- `StatTranslationHistory` — cached AI stat translations between world pairs

**WorldNpcState:**
NPC disposition is **world-scoped**. The same NPC in two different worlds can have different feelings toward a player. `OverlayWorldNpcStateAsync()` loads per-world disposition before conversations; `PersistWorldNpcStateAsync()` saves it after each turn.

**Stat translation:**
When a player travels to a world with different rules, `RealmTravelService.TransferPlayerAsync()`:
1. Snapshots current stats (`WorldStatSnapshot`)
2. Calls `NarratorService.TranslateStatsAsync()` — AI maps stats using `SemanticTags`
3. Applies translated stats and moves player to the destination world
4. On return home, restores original stats (with level-up adjustments)

**Portal mechanics:**
- `WorldPortal` defines connections between rooms in different worlds
- `CommandParser.PortalTravelRegex()` detects portal commands
- Portals can have restrictions: `MinLevel`, `RequiredCompletedQuests`, `IsAdminOnly`

**Content scoping:**
- `Room.WorldIds`, `Npc.WorldIds`, `QuestDefinition.WorldIds` — entities belong to one or more worlds
- All registry types (`SpellDefinition`, `ClassDefinition`, `RaceDefinition`, `ItemTemplate`, `MonsterTemplate`) have `WorldIds`
- `StoryEntry.WorldId` and `CombatState.WorldId` ensure state isolation per world
- YAML seed files support `world_ids:` field (defaults to `["default-world"]` if omitted)

**Narrator world context:**
All four narrator methods inject world context (name, description, rules summary) via `BuildWorldContextBlock()` or `GetCurrentWorldContextBlockAsync()`. Story history is fetched with world filtering via `GetRecentStoryForRoomAsync(roomId, worldId)`.

---

## Testing

```bash
dotnet test                           # Run all tests
dotnet test tests/GAE.Engine.Tests    # Unit tests only
dotnet test tests/GAE.Integration.Tests  # Integration tests (requires no live LM Studio)
npm run test:e2e:safe                 # Browser tests (requires running server)
npm run test:e2e:update-snapshots:safe  # Update visual baselines
```

**Test philosophy:**
- Unit tests mock INarratorService and IProbabilityEngine via Moq
- Integration tests use WebApplicationFactory with a real in-memory state manager
- Never mock the database (there isn't one) — use InMemoryStateManager directly
- Browser tests use Playwright; always use the `:safe` variants to avoid oversized payloads

---

## Code Style

- C# 12+, .NET 9, nullable reference types enabled, implicit usings
- Async all the way down. CancellationToken on every async method.
- Every public method gets an XML doc comment explaining WHAT and WHY, not HOW.
- State mutations are batched — save player and room once after all changes, not per-change.
- Never hardcode stat names in the UI. Read from the character definition / game-rules config.
- Log at the right level: Debug for hot paths, Information for significant events, Warning for recoverable failures, Error for unhandled exceptions.

---

## Known Behavioral Rules for Coding Agents

1. **Never add canned fallback responses.** If the narrator fails, log the error and use `BuildContextualFallbackNarration()` or `BuildLocalFreeFormFallbackResponse()` — these are generic but grounded in room context.
2. **The sidebar is admin-only.** The User Flow view is single-column, full-width, no sidebar.
3. **Entity summarization is mandatory everywhere.** Use `SummarizeEntities()` in C# and `_summarizeCounts()` in JS. Never list duplicate entity names individually.
4. **The story log does not show room metadata.** `_stripRoomMetadata()` must strip anything that looks like a room dump before rendering.
5. **Input is owned by the stream.** `_startStreaming()` disables the command input. The `finally` block in `executeUserCommand` must NOT re-enable it if `UI._streamNode` is set.
6. **Disposition persists and is dual-layer.** When a conversation ends, `ApplyInteractionUpdate()` saves both `Npc.Disposition` (flat) and `Npc.DispositionState` (rich). Always keep them in sync. The rich state is preferred for narrator prompts.
7. **All action results must go through `afterCommand()`.** Never update player/room state from the UI without going through the server.
8. **NPC knowledge is scoped.** Never give an NPC access to wiki knowledge outside their `KnowledgeScopes`. Use `BuildScopedContextAsync()` for conversations, `BuildContextAsync()` for generic narration.
9. **Dynamically generated NPCs must have KnowledgeScopes and DispositionState.** Infer scopes from faction + environment tags. Initialize DispositionState with sensible defaults (see `GenerateNpcAsync` for the pattern).
10. **Wiki is the narrator's memory.** If a significant world event occurs (NPC death, faction shift, discovery), consider writing it to the wiki so future narrator calls have context.
11. **WorldNpcState is world-scoped.** NPC disposition differs per world. Always call `OverlayWorldNpcStateAsync()` before conversation and `PersistWorldNpcStateAsync()` after each turn. Never assume NPC feelings are global.
12. **Content must be world-tagged.** New rooms, NPCs, quests, and registry items must have `WorldIds` set. Default to `[WorldDefaults.DefaultWorldId]` if unspecified.
13. **Stat translation is AI-driven.** When transferring players between worlds with different stat systems, use `RealmTravelService` — never manually map stats. SemanticTags on StatConfig guide the AI translation.
14. **Portal restrictions are enforced.** Check `MinLevel`, `RequiredCompletedQuests`, and `IsAdminOnly` before allowing portal travel. The engine handles this in `GameEngine.TravelToWorldAsync()`.
