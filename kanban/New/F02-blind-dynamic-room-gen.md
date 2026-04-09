# F02 — Blind Adventure: Dynamic Room Generator

**Category:** Feature — Blind Adventure Mode
**Effort:** Large
**Touches:** `GAE.Engine/`, `GAE.Narrator/`, room models

## What

When a player moves in a direction that has no predefined room, the engine asks the narrator to generate one. The narrator uses the storyline context, adjacent rooms, and player history to create a room that makes sense.

## Depends On

- **F01** (Storyline Context Object) — needs the storyline to generate coherent rooms

## Design

Flow:
1. Player types `!go north`
2. Engine checks: does a room exist to the north? → No
3. Engine calls narrator: "Generate a room to the north of [current room]. Storyline: [context]. Player has visited: [room list]. Plot beat to weave in: [next undelivered beat, if appropriate]."
4. Narrator returns: room name, description, exits (directions only), NPCs present (optional), items present (optional)
5. Engine creates the room, wires exits bidirectionally, moves player in

## Acceptance Criteria

- [ ] New method: `GenerateRoomAsync(currentRoom, direction, storylineContext, visitedRooms)` on the narrator
- [ ] Narrator prompt template crafted for room generation (separate from normal narration)
- [ ] Generated room registered in the room registry with proper exits
- [ ] Bidirectional exit wiring (new room points back to where you came from)
- [ ] Player moved into the new room after generation
- [ ] Generated rooms persisted — revisiting uses the saved version, not a new generation
- [ ] Fallback: if narrator fails, generate a minimal placeholder room ("A dimly lit passage...")
- [ ] Test: movement into unknown direction triggers generation
- [ ] Test: backtracking to a generated room returns the same room

## Notes

- Evaluate `DungeonGenerator.cs` (36KB, already exists) — it may handle graph/layout concerns that complement this. If it can serve as the structural backbone (room graph, connectivity) while the narrator handles flavor, that's ideal.
- Keep narrator prompts tight. Room generation should return structured data (JSON or parseable text), not free-form paragraphs.
