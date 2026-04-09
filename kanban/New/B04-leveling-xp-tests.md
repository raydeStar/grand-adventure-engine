# B04 — Leveling & XP Test Coverage

**Category:** Blocker
**Effort:** Small-Medium
**Touches:** `tests/GAE.Engine.Tests/`, `GAE.Engine/GameEngine.cs`, `GameRulesConfig`

## What

XP formula and per-level HP/MP scaling exist in config, but no tests verify the full leveling flow: earn XP → hit threshold → level up → stats increase.

## Why This Blocks MVP

If leveling is broken, players grind with no payoff. Or worse — they level up and their stats don't change, or they skip levels, or the XP threshold math is wrong. This needs to be proven correct.

## Acceptance Criteria

- [ ] Test: XP earned from combat is correct (based on enemy CR or config)
- [ ] Test: XP earned from quest turn-in is correct
- [ ] Test: level-up triggers at the right XP threshold
- [ ] Test: HP and MP increase on level-up per config formula
- [ ] Test: multiple level-ups in one XP grant (e.g., big quest reward) handled correctly
- [ ] Test: level-up doesn't reset current HP/MP (only max increases)
- [ ] Test: level cap respected (if one exists)

## Notes

- Quest XP rewards are tested in `QuestEngineTests`, but XP *accumulation* on the player character is not.
- Check `GameRulesConfig` for the XP formula (lines 114-120 area).
- This pairs well with B03 (death tests) — can share test fixtures.
