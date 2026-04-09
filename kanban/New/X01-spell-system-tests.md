# X01 — Spell System Testing & Scaling

**Category:** Post-MVP
**Effort:** Medium
**Touches:** `GAE.Engine/GameEngine.cs`, spell vetting, narrator

## What

Spell vetting exists (AI approves/rejects custom spells) but when the narrator is unavailable, fallback spells don't scale properly. The spell system also lacks dedicated tests.

## Acceptance Criteria

- [ ] Fallback spell damage scales with player level
- [ ] Tests for spell creation, vetting, casting, and mana cost
- [ ] Tests for fallback spell generation when narrator is down
- [ ] Spell list viewable by player (`!spells` or similar)
