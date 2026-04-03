---
name: GAE Architecture Overview
description: Key architecture decisions for Grand Adventure Engine — LM Studio, Wiki.js, no DB, in-memory state
type: project
---

Grand Adventure Engine (GAE) is a .NET 10 project with these key architectural choices:

- **LM Studio** is the AI backend (OpenAI-compatible API at localhost:1234)
- **Wiki.js** at localhost:3000 with GraphQL API for world lore persistence
- **No database** — state is in-memory, journaled to disk (InMemoryStateManager / JournaledStateManager)
- **WorldKnowledgeBuilder is nullable** in NarratorService — narrator works without wiki, just loses lore context
- **Disposition is dual-layer** — Npc.Disposition (flat string) and Npc.DispositionState (rich object) must stay in sync
- **NPC knowledge scoping** — NPCs only know wiki lore matching their KnowledgeScopes tags

**Why:** Project is day-old, scope is intentionally bounded — AI narrator/DM for a game world.
**How to apply:** Respect the no-DB constraint, keep wiki integration gracefully degradable, maintain dual disposition sync.
