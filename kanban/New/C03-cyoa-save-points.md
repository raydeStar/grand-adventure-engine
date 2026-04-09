# C03 — CYOA: Save Points & Death Rewind

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Engine/`, player state, CYOA models

## What

Some choices kill you. When that happens, you rewind to the last save point — not all the way back to the start. Save points are created automatically at key moments.

## Depends On

- **C01** (Game Mode & Simplified State) — save points stored on player state
- **C02** (Choice Tree) — save points are special nodes in the tree

## Design

### Save Point Creation
- **Auto-save** every N choices (configurable, default 5)
- **Narrator-triggered**: narrator can flag a node as a save point ("You find a campfire — this feels like a safe moment")
- Save captures: current node, health, inventory snapshot

### Death & Rewind
1. Narrator signals death in its response (a flag or sentinel in structured output)
2. Engine shows the death narration
3. Engine rewinds to last save point: restore node, health, inventory from snapshot
4. Player sees: "You wake up at [save point description]. The memory of [death] lingers..."
5. Player gets the same choices again (or narrator regenerates — design choice)

### Going Back Voluntarily
- Player can type `!save` to see their save points
- Player can type `!load <n>` to rewind to a specific save point
- Loading a save point discards all progress after it

## Acceptance Criteria

- [ ] Auto-save every N nodes (configurable)
- [ ] Narrator can flag nodes as save points
- [ ] Save snapshot captures: node ID, health, inventory copy
- [ ] Death detected from narrator response → rewind triggered
- [ ] Rewind restores health, inventory, and position to save state
- [ ] Death narration shown before rewind
- [ ] Rewind narration shown after restore ("You find yourself back at...")
- [ ] `!save` lists available save points
- [ ] `!load` rewinds to chosen save point
- [ ] Test: death → rewind to last save → player state matches save snapshot
- [ ] Test: voluntary load to earlier save discards later progress
- [ ] Test: auto-save triggers at correct interval

## Notes

- Save points are checkpoints, not full game saves. They're CYOA-mode only.
- Don't keep unlimited saves — cap at 5-10 most recent to avoid memory bloat.
- When rewinding, re-present the original choices. Don't regenerate — the player wants to pick differently this time.
