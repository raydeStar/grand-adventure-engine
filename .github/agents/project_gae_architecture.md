---
name: GAE Architecture Overview
description: Current architecture notes for Grand Adventure Engine
type: project
---

Grand Adventure Engine (GAE) is a .NET project with these key architectural choices:

- **LM Studio / OpenAI-compatible HTTP** is the narrator backend by default.
- **PostgreSQL via EF Core** is the primary persistence path.
- **YAML seeds** load rules, lore, quests, registry content, and demo world data into the content registry.
- **Disposition is dual-layer**: `Npc.Disposition` stays in sync with `Npc.DispositionState`.
- **NPC knowledge is scoped**: conversations use lore matching the NPC's `KnowledgeScopes`.
- **Multi-world state is isolated** by world IDs, active world context, and world-scoped NPC state.

Use the current source and AGENTS.md as authoritative when these notes drift.
