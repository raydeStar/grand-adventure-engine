# X03 — NPC Disposition Affecting Combat

**Category:** Post-MVP
**Effort:** Small-Medium
**Touches:** `GAE.Engine/GameEngine.cs`, NPC disposition model

## What

NPC dispositions track how NPCs feel about the player, but this doesn't currently affect combat behavior. A hostile NPC who hates you should fight harder (or ambush you). A friendly NPC pushed to hostility should fight reluctantly.

## Acceptance Criteria

- [ ] Disposition intensity modifies NPC combat stats (damage bonus/penalty, initiative modifier)
- [ ] Extremely negative disposition can trigger unprovoked attacks
- [ ] NPCs with positive disposition may flee or surrender instead of fighting to the death
- [ ] Tests for disposition-modified combat scenarios
