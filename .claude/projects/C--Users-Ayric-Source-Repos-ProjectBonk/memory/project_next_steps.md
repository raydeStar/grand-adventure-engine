---
name: GAE Next Steps (from 2026-04-02 handoff)
description: Priority-ordered task list from Cowork handoff — NPC wiki publishing, disposition decay, event writing, character cards, sidebar bug
type: project
---

Priority-ordered next steps from the 2026-04-02 Cowork handoff:

1. **NPC Wiki Auto-Publishing** — When GenerateNpcAsync/GenerateRoomAsync creates NPCs, write their page to wiki. Closes the knowledge pipeline loop.
2. **Disposition Decay Between Sessions** — DecayTowardBaseline(TimeSpan) on NpcDispositionState. Intensity drifts toward baseline (~40) over time.
3. **Story Event Wiki-Writing** — Significant events (NPC killed, item stolen, faction changes) get timestamped wiki entries under events/{id}.
4. **Data-Driven Character Definition Card** — Move hardcoded stats to dictionary-based system driven by game-rules.yaml. Biggest refactor.
5. **Sidebar Bug** — Admin sidebar reappears in User Flow view. Check layout component in wwwroot/.

**Why:** These were identified during the Cowork design session as the most impactful next features.
**How to apply:** Work in this priority order unless Ayric redirects.
