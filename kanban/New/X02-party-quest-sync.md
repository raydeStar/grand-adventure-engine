# X02 — Party Quest Sync

**Category:** Post-MVP
**Effort:** Medium
**Touches:** `GAE.Engine/QuestEngine.cs`, party system

## What

Party quest progress model exists (`PartyQuestProgress`) but updates aren't always broadcast to all party members. When one player advances a shared objective, others should see it.

## Acceptance Criteria

- [ ] Shared objective progress broadcasts to all party members
- [ ] Party members see quest update notifications in their threads
- [ ] Tests for multi-player quest advancement
- [ ] Edge case: party member offline when progress happens → sees update on next action
