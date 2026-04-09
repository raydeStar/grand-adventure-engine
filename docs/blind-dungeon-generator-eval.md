# DungeonGenerator Evaluation for Blind Adventure

This note records the F04 spike on whether `src/GAE.Engine/DungeonGenerator.cs` should be reused for Blind Adventure mode.

## Recommendation

Adapt it for graph generation, but do not use it as-is for Blind Adventure room creation.

The current generator is good at building a connected dungeon skeleton and persisting it as normal runtime `Room` objects. It is not a good fit for room-by-room narrator-driven discovery because it generates the whole dungeon up front and bakes names, descriptions, enemies, and loot into every room immediately.

## What It Produces

`DungeonGenerator` produces a complete multi-floor dungeon as persisted `Room` objects.

- Entry point: `GenerateFullDungeonAsync(...)`
- Output: saved rooms with ids, names, descriptions, exits, items, NPCs, environment tags, and `WorldIds`
- Content source: registry-backed templates with procedural fallback generation for weapons, armor, monsters, bosses, and potions

Relevant code:

- `GenerateFullDungeonAsync(...)` in `src/GAE.Engine/DungeonGenerator.cs`
- `GenerateFloor(...)` in `src/GAE.Engine/DungeonGenerator.cs`
- `CreateRoom(...)` in `src/GAE.Engine/DungeonGenerator.cs`

## Answers to the F04 Questions

### 1. What does it actually produce?

It produces a fully materialized dungeon, not just a layout. Each room already has flavor text, room type, enemies, loot, exits, and persistence-ready state.

### 2. Does it guarantee connectivity?

Yes.

- Main-path rooms are wired linearly with bidirectional exits.
- Branch rooms are attached to existing main-path rooms with return exits.
- Floors are linked vertically through the previous floor's boss room.

This makes the graph safe to traverse and a good candidate for skeleton reuse.

### 3. Can it generate incrementally?

No.

The generator is all-at-once. It decides dungeon size first, creates every room, wires all exits, and saves the entire result in one batch. There is no on-demand hook for "player moved into an unexplored direction, generate one room now."

### 4. Does its output align with the existing room model?

Yes, very well.

It already outputs standard runtime `Room` objects and saves them through `IStateManager`. From a data-shape perspective, it fits the engine cleanly.

### 5. Could it be used as a Blind Adventure skeleton?

Partially.

It is a good source of graph and connectivity rules. It is a poor source of final room content for Blind Adventure because Blind mode wants narrator-authored discovery on entry, not pre-authored room flavor and pre-spawned encounters for the whole dungeon.

## Strengths

- Connectivity is already solved.
- Registry integration is already solved.
- Difficulty scaling is already solved.
- Output already matches persisted runtime rooms.

## Limitations for Blind Adventure

- Batch generation only; no room-by-room generation path.
- Room names and descriptions are baked in too early.
- NPC and loot placement happen before the player discovers the room.
- The algorithm is tuned for pre-generated dungeons, not narrator-led progressive exploration.

## Decision

Adapt, do not use directly.

The most promising reuse path is:

1. Extract the graph-building logic from `GenerateFloor(...)` into a dungeon skeleton generator.
2. Keep room ids and exit connectivity.
3. Delay room naming, descriptions, NPCs, and item placement until the player actually enters a room.
4. Let the Blind Adventure narrator fill that room on demand.

## Practical Use Cases

- Use as-is: pre-generated quest dungeons or traditional multi-floor crawls.
- Adapt: Blind Adventure skeleton generation.
- Skip for Blind Adventure content generation: yes.

## Suggested Follow-Up

If Blind Adventure mode proceeds, the next extraction target should be the connectivity portion of `GenerateFloor(...)`, not `CreateRoom(...)`.