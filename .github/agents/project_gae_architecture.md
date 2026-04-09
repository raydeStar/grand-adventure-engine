---
name: GAE Architecture Overview
description: Key architecture decisions for Grand Adventure Engine — LM Studio, no DB, in-memory state
type: project
---

Grand Adventure Engine (GAE) is a .NET 10 project with these key architectural choices:

- **LM Studio** is the AI backend (OpenAI-compatible API at localhost:1234)
- **No database** — state is in-memory, journaled to disk (InMemoryStateManager / JournaledStateManager)
- **WorldKnowledgeBuilder is nullable** in NarratorService — narrator works without lore, just loses context
- **Disposition is dual-layer** — Npc.Disposition (flat string) and Npc.DispositionState (rich object) must stay in sync
- **NPC knowledge scoping** — NPCs only know lore matching their KnowledgeScopes tags
- **World knowledge** comes from YAML lore seeds loaded into a ContentRegistry at startup

**Why:** Project is day-old, scope is intentionally bounded — AI narrator/DM for a game world.
**How to apply:** Respect the no-DB constraint, keep lore integration gracefully degradable, maintain dual disposition sync.
