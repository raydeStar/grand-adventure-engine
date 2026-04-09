# C02 — CYOA: Choice Tree & Branch Generation

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Large
**Touches:** `GAE.Engine/`, `GAE.Narrator/`, new CYOA models

## What

The core mechanic of CYOA: each narration presents 2-4 choices. The player picks one. The narrator generates the next node based on what happened. No pre-authored tree needed — it's generated on the fly.

## Depends On

- **C01** (Game Mode & Simplified State)

## Design

### Choice Node Model
```
NodeId: "chapter-2-the-bridge"
NarrationText: "The rope bridge sways over a misty chasm..."
Choices:
  - id: "cross-carefully"
    text: "Cross slowly, gripping the ropes"
  - id: "run-across"  
    text: "Sprint across before you lose your nerve"
  - id: "find-another-way"
    text: "Look for another path along the cliff"
ParentNodeId: "chapter-2-entrance"
IsSavePoint: false
```

### Flow
1. Player sees narration + choices
2. Player picks a choice (by number or text)
3. Engine records choice in history
4. Engine sends choice + context to narrator
5. Narrator generates next node (narration + new choices)
6. Repeat

### Narrator Input for Generation
- The choice the player made
- Current health state and inventory
- Story context (theme, tone, where we are in the arc)
- Recent choice history (last 3-5 nodes for continuity)
- Whether to introduce a save point (every N nodes or at key moments)

## Acceptance Criteria

- [ ] Choice node model defined in core
- [ ] Player can see narration and numbered choices
- [ ] Player picks a choice by number (`1`, `2`, `3`) or by typing the choice text
- [ ] Engine records choice and advances to next node
- [ ] Narrator generates next node with narration + new choices
- [ ] Choice history tracked on player state
- [ ] Fallback if narrator fails: generic "continue forward" or "go back" choices
- [ ] Test: full 3-node choice chain works end to end
- [ ] Test: invalid choice number shows error and re-presents options

## Notes

- Choices should feel meaningful but the narrator has freedom. "Cross slowly" vs "sprint across" might lead to the same place with different consequences, or completely different paths.
- The narrator decides how many choices to offer (2-4). Engine validates the response has at least 2.
