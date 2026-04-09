# X05 — Wiki Read-Back (Wiki → Engine Sync)

**Category:** Post-MVP
**Effort:** Large
**Touches:** `GAE.WikiSync/`, `GAE.Engine/`, content loading

## What

WikiSync currently only writes game state *to* the wiki. The dream is editing NPCs, rooms, quests, and lore in the wiki and having the engine pick up changes. This makes the wiki a real content management system.

## Acceptance Criteria

- [ ] Engine can read room definitions from wiki pages
- [ ] Engine can read NPC definitions from wiki pages
- [ ] Engine can read quest definitions from wiki pages
- [ ] Sync runs on a schedule or on-demand (not every request)
- [ ] Conflict resolution: wiki edits vs in-memory state
- [ ] Wiki page format documented so game masters know how to edit
