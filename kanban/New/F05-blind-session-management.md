# F05 — Blind Adventure: Session & Guardrails

**Category:** Feature — Blind Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Engine/`, `GAE.Discord/`, session/state management

## What

A blind adventure is a bounded session — it starts, the player explores generated rooms, and it ends. This task covers the lifecycle: starting a blind adventure, tracking progress, and ending it cleanly.

## Depends On

- **F01** (Storyline Context)
- **F02** (Dynamic Room Generator)

## Acceptance Criteria

- [ ] New command or option to start a blind adventure: `!adventure start <storyline-id>` or similar
- [ ] Starting room generated from `StorylineContext.StartingRoomDescription`
- [ ] Session tracks: rooms generated, rooms visited, plot beats delivered, current room count vs max
- [ ] Adventure ends when: narrator signals conclusion, max rooms reached, or player types `!adventure end`
- [ ] On end: summary narrated ("You escaped the manor with the truth..."), session stats shown (rooms explored, enemies fought, etc.)
- [ ] Player returns to their normal game state after adventure ends (if they had one)
- [ ] Guard: can't start a blind adventure while already in one
- [ ] Guard: max rooms enforced — narrator prompted to conclude as limit approaches
- [ ] Test: start → explore → end lifecycle
- [ ] Test: max room cap triggers ending

## Notes

- Blind adventure could be a separate "mode" on the player state, or a separate session object. Keep it simple — a flag + session data on the player is probably enough.
- Consider: does the player keep items/XP earned during a blind adventure? Probably yes for MVP.
