# Handoff: Cowork → Claude Code

> "The butler has laid out your wardrobe, sir. The rest is a matter of wearing it well." — Sir Thaddeus

This document captures the current state and immediate next steps for continuing work in Claude Code.

## What Was Done (Cowork Session, 2026-04-02)

### 1. Wiki-to-Narrator Knowledge Pipeline
- Extended `IWikiService` with `SearchAsync` and `GetPagesAsync` (GraphQL read operations)
- Implemented both in `WikiService.cs`
- Created `WorldKnowledgeBuilder` — pre-fetches room/NPC/faction/region lore from Wiki.js
- Wired into all four narrator methods (NarrateAction, ProcessFreeForm, ProcessConversation, ProcessCombat)
- Added lore-seed wiki sync at startup (world overview, regions, factions, starting room → wiki pages)

### 2. NPC Knowledge Scoping
- Added `KnowledgeScopes` field to `Npc` model — list of tags controlling wiki access
- Created `BuildScopedContextAsync` in WorldKnowledgeBuilder — filters wiki pages by NPC scopes
- Conversation prompt now uses scoped knowledge (not generic)
- System prompt includes KNOWLEDGE BOUNDARIES instructions — NPC deflects when asked about unknown topics
- Dynamic NPC generation infers scopes from faction + environment tags

### 3. Rich NPC Disposition System
- Added `NpcDispositionState` class: Emotion, Intensity (0-100), Baseline, Reason
- Added `DispositionState` to `Npc` model (coexists with flat `Disposition` for backward compat)
- Added `DispositionState` to `InteractionUpdate` so AI can return rich emotional shifts
- Updated `ApplyInteractionUpdate` in GameEngine to keep both representations in sync
- Rewrote conversation system prompt with DISPOSITION ENGINE instructions
- NarrateActionAsync now shows rich disposition in NPC detail strings

### 4. UX Bug Fixes (earlier in session)
- Fixed flash/reopen bug in `executeUserCommand` finally block
- Fixed entity spam in story log `_stripRoomMetadata()`
- Rewrote all three narrator system prompts for deeper NPC dialogue

### 5. AGENTS.md Rewrite
- Updated to reflect all new systems (knowledge pipeline, scoping, disposition)
- Added behavioral rules 8-10

## Files Changed

```
src/GAE.Core/Models/Npc.cs                    — KnowledgeScopes, DispositionState, NpcDispositionState class
src/GAE.Core/Models/InteractionUpdate.cs       — DispositionState field
src/GAE.Core/Interfaces/IWikiService.cs        — SearchAsync, GetPagesAsync, WikiSearchResult record
src/GAE.WikiSync/WikiService.cs                — Implemented SearchAsync, GetPagesAsync (GraphQL)
src/GAE.Narrator/WorldKnowledgeBuilder.cs      — NEW FILE: BuildContextAsync, BuildScopedContextAsync, SearchContextAsync
src/GAE.Narrator/NarratorService.cs            — Wiki knowledge injection in all prompts, disposition helpers, NPC generation defaults
src/GAE.Engine/GameEngine.cs                   — ApplyInteractionUpdate handles rich disposition
src/GAE.Dashboard.Api/Program.cs               — DI registration, lore-seed wiki sync, LoreNpc DTO expanded
config/lore-seed.yaml                          — Mara has knowledge_scopes and disposition
AGENTS.md                                      — Updated with new systems and rules
```

## First Thing To Do In Code

```bash
dotnet build
```

If it compiles clean, run:

```bash
dotnet test
```

All changes are additive — no existing interfaces were broken, only extended. But I couldn't verify compilation in the Cowork sandbox. Flush out any issues here first.

## Immediate Next Steps (Priority Order)

### 1. NPC Wiki Auto-Publishing
When `GenerateNpcAsync` or `GenerateRoomAsync` creates a new NPC, write their page to the wiki:
```csharp
await _wiki.CreateOrUpdatePageAsync($"npcs/{npc.Id}", npc.Name,
    $"# {npc.Name}\n\n{npc.Personality}\n\nFaction: {npc.Faction}");
```
This closes the loop — the knowledge pipeline can now read about NPCs it created.

### 2. Disposition Decay Between Sessions
Add a `DecayTowardBaseline(TimeSpan elapsed)` method on `NpcDispositionState`.
Intensity drifts toward a baseline level (e.g., 40) over time. A kiss that spiked flustered to 65
should fade to ~45 by next visit. Call it when a room is loaded or a conversation starts.

### 3. Story Event Wiki-Writing
When significant events occur (NPC killed, item stolen, faction relationship changed),
write a timestamped entry to `wiki/events/{roomId}` or `wiki/events/{npcId}`.
The narrator's SearchContextAsync will find these when relevant topics come up.

### 4. Data-Driven Character Definition Card
Stats are hardcoded in `PlayerCharacter.cs` (Str, Dex, Con, etc.).
Move to a dictionary-based system driven by `game-rules.yaml`.
This is the biggest refactor — touch PlayerCharacter, all narrator prompts, the UI stat display, and game-rules.yaml.

### 5. Sidebar Investigation
The admin sidebar keeps reappearing in the User Flow view.
Check the layout component in wwwroot/ for conditional rendering bugs.

## Architecture Notes For Code

- **LM Studio** is the AI backend (OpenAI-compatible API at localhost:1234). Currently running a 2B model.
- **Wiki.js** is at localhost:3000 with GraphQL API. Used for world lore persistence.
- **No database.** State is in-memory, journaled to disk. `InMemoryStateManager` / `JournaledStateManager`.
- **WorldKnowledgeBuilder is nullable** in NarratorService — if wiki is down, narrator still works, just without lore context.
- **Disposition is dual-layer** — always keep `Npc.Disposition` (flat string) and `Npc.DispositionState` (rich object) in sync.
