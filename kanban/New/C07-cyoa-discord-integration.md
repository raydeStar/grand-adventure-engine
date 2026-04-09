# C07 — CYOA: Discord Integration

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Discord/`, command handling, thread management

## What

Wire CYOA mode into the Discord bot so players can start, play, and finish a CYOA adventure from their Discord thread.

## Depends On

- **C01** through **C06** — all CYOA engine work

## Design

### Starting a CYOA Adventure
- `/cyoa` slash command (or `!cyoa start`)
- Player can optionally describe a theme: `!cyoa start "a heist in a floating city"`
- If no theme given, use a random or default storyline
- Thread renamed to indicate CYOA mode: `📖 Elena's Adventure`

### Playing
- Narration displayed as a Discord embed (looks like a book page)
- Choices shown as numbered list below the narration
- Player types `1`, `2`, `3`, etc. to choose (or types the choice text)
- Health and inventory shown in embed footer or sidebar
- Save point notifications: "📌 Save point reached"

### Special Commands in CYOA Mode
- `!save` — list save points
- `!load <n>` — rewind to save point
- `!inventory` / `!inv` — show items
- `!health` — show current health state
- `!cyoa end` — quit the adventure early

### Ending
- Final narration displayed as a special embed
- Adventure summary shown
- Thread returned to normal mode (or archived)

## Acceptance Criteria

- [ ] `/cyoa` slash command registered and functional
- [ ] CYOA narration displayed as Discord embeds (distinct from full RPG narration)
- [ ] Choice selection works by number
- [ ] Invalid choice number shows error and re-presents options
- [ ] Save/load commands work in CYOA mode
- [ ] Inventory and health commands work in CYOA mode
- [ ] Full RPG commands (`!attack`, `!cast`, etc.) disabled or show "not available in story mode"
- [ ] Adventure end cleans up session state
- [ ] Thread visually indicates CYOA mode (rename or prefix)

## Notes

- Discord embeds are great for CYOA — they make each "page" feel distinct from chat noise.
- Consider using embed color to indicate health: green (healthy), yellow (hurt), orange (critical), red (dead/rewind).
- This is the final integration task — it ties everything together. Don't start this until C01-C06 are solid.
