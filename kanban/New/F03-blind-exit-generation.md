# F03 — Blind Adventure: Exit & Connectivity Logic

**Category:** Feature — Blind Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Engine/`, room models

## What

When the narrator generates a room, it suggests exits. The engine needs to wire those exits correctly, avoid orphaned rooms, and prevent the map from becoming an incoherent mess.

## Depends On

- **F02** (Dynamic Room Generator)

## Design Decisions

- **Narrator suggests exits** (e.g., "north, east, down") but the engine validates them
- **Back-link always exists** — if you came from the south, the new room always has a south exit back
- **Forward exits are promises** — "north" means "there will be a room to the north if you go there" (generated on demand)
- **Dead ends are valid** — the narrator can create rooms with only one exit (the way back)
- **Loops are allowed** — the narrator can connect a room back to a previously visited room if it makes narrative sense

## Acceptance Criteria

- [ ] Back-link to origin room always created
- [ ] Forward exits stored as "pending" — no room behind them yet, generated on first traversal
- [ ] Narrator exit suggestions validated (no duplicate directions, no impossible connections)
- [ ] `!look` shows available exits including unexplored ones
- [ ] Room count tracked against `StorylineContext.MaxRooms` — narrator told to wrap up as limit approaches
- [ ] Test: generated room has back-link
- [ ] Test: forward exit leads to new generation on traversal
- [ ] Test: max room cap triggers narrator to generate a "final" room with no forward exits

## Notes

- The narrator doesn't need to know about the full graph. Just: "The player came from the south. This room has exits to the north and east. There are 3 rooms left before the story should end."
