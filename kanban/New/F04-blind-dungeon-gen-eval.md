# F04 — Blind Adventure: Evaluate DungeonGenerator Integration

**Category:** Feature — Blind Adventure Mode
**Effort:** Small (research/spike)
**Touches:** `GAE.Engine/DungeonGenerator.cs`

## What

`DungeonGenerator.cs` is a 36KB file that already exists but isn't wired into the main game flow. Before building more dynamic room infrastructure, evaluate whether it can serve as the structural backbone for Blind Adventure mode.

## Questions to Answer

1. What does DungeonGenerator actually produce? (Room graph? Layout? Tile map?)
2. Does it handle connectivity (ensuring all rooms are reachable)?
3. Can it generate incrementally (room-by-room) or only all-at-once?
4. Does its output model match the existing room registry format?
5. Could it pre-generate a *skeleton* that the narrator then fills with flavor text?

## Acceptance Criteria

- [ ] Read through DungeonGenerator.cs and document what it does
- [ ] Write a findings summary (can be a comment in this file or a short doc)
- [ ] Decision: use it, adapt it, or skip it for Blind Adventure mode
- [ ] If "use it" — list what changes are needed to integrate
- [ ] If "skip it" — note why and what it might be useful for instead

## Notes

- This is a spike/research task, not a build task. Timebox it.
- The answer might be: "it's great for pre-generated dungeons but not for on-the-fly room creation" — that's fine, it just means it solves a different problem.
