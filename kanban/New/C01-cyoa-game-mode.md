# C01 — CYOA: Game Mode Flag & Simplified Player State

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Core/` models, `GAE.Engine/`, player state

## What

The engine needs to know "this session is CYOA, not full RPG." CYOA mode uses radically simpler mechanics: no HP/MP/XP numbers, just a general health state, a flat inventory, and a choice tree.

## Why First

Every other CYOA task depends on the engine knowing which mode it's in. The simplified player state is the foundation everything else builds on.

## Design

### Game Mode
- Add a `GameMode` enum: `FullRpg`, `ChooseYourOwnAdventure`
- Player or session carries the mode flag
- Engine checks mode before applying mechanics (no XP grants in CYOA, no stat checks)

### Simplified Player State (CYOA only)
```
Name: "Elena"
Health: Healthy | Hurt | Critical | Dead
Inventory: ["Rusty Key", "Torch", "Mysterious Letter"]
CurrentNode: "chapter-3-the-bridge"
SavePoints: ["chapter-1-start", "chapter-2-the-fork"]
ChoiceHistory: [{node, choiceText, timestamp}, ...]
```

No HP numbers, no MP, no XP, no equipment slots, no gold, no stats.

## Acceptance Criteria

- [ ] `GameMode` enum added to core models
- [ ] Player state extended (or new CYOA state model) with: Health (enum), Inventory (string list), CurrentNode, SavePoints, ChoiceHistory
- [ ] Engine respects game mode — CYOA players skip combat rolls, stat checks, XP, etc.
- [ ] Starting a CYOA session sets mode and initializes simplified state
- [ ] Test: CYOA player has no HP/MP/XP fields populated
- [ ] Test: mode flag prevents full-RPG mechanics from running

## Notes

- Health enum is intentionally vague. "Hurt" might mean you fell off a ledge or got bitten — the narrator describes it, the engine just tracks the level.
- Inventory is just strings. No item models, no weight, no stats. "Torch" is a narrative flag, not a game object.
