# X06 — Race & Class-Specific Abilities

**Category:** Post-MVP
**Effort:** Large
**Touches:** `GAE.Core/` models, `GAE.Engine/GameEngine.cs`, character creation

## What

Characters have race and class but these don't grant specific abilities or traits. A rogue should have different options than a paladin. An elf should have different innate traits than a dwarf.

## Acceptance Criteria

- [ ] Race model includes innate traits (e.g., darkvision, poison resistance)
- [ ] Class model includes abilities unlocked at specific levels
- [ ] Abilities usable via `!use <ability>` command
- [ ] Traits applied passively (e.g., resistance modifies incoming damage)
- [ ] Character creation assigns race/class traits automatically
- [ ] Tests for trait application and ability usage
