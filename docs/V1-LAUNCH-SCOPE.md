# V1 Launch Scope

This document defines the recommended `V1` boundary for Grand Adventure Engine.
It is intentionally narrower than the full architecture vision so we can launch
the project with confidence instead of shipping three half-finished products at once.

---

## V1 In One Sentence

Grand Adventure Engine V1 is a launchable Discord-first text RPG with a stable
web dashboard, one polished default world, deterministic mechanics under AI
narration, and enough admin tooling to run live sessions without touching code.

---

## The Core Promise

V1 should let a player:

- create a character in natural language
- explore a real world with persistent state
- move, look, talk, fight, loot, shop, rest, quest, level, and respawn
- receive grounded narration on every action
- resume progress across sessions

If the project does those things reliably, it is a real launchable product.

---

## What Is In Scope For V1

### Core Game

- Discord bot play
- dashboard play/login/admin flows
- PostgreSQL persistence
- one default world with curated seed content
- character creation
- exploration and room traversal
- NPC conversation with disposition memory
- combat, death, and respawn
- inventory, equipment, consumables, and shops
- quests, rewards, and level progression
- lore discovery / narrator world context
- narrator retry + fallback behavior

### Operational Readiness

- setup docs that match reality
- health endpoints that reflect narrator + database state
- a repeatable smoke-test flow for user and admin paths
- CI for build/test so launch confidence does not depend on memory
- a clear license and README that describe the current product honestly

---

## What Is Explicitly Out Of Scope For V1

These features may remain in the repo, but they are not part of the public
launch promise and should not block launch.

- CYOA mode as a marketed feature
- multi-world as a marketed feature
- fully arbitrary stat schemas
- semantic travel commands like `follow road` or `take trail`
- wiki sync / wiki-driven content as a supported system
- multiple launch-ready game modes

The biggest rule: V1 ships one polished game, not a menu of experiments.

---

## The Hard Calls

### 1. Dynamic Room Generation

Recommended V1 decision:

- Do not make dynamic room generation a requirement of the main RPG launch.
- Keep the main launch centered on the curated default world.
- If we want one differentiating stretch feature before launch, make it
  `Blind Adventure`, and only `Blind Adventure`.

Why:

- The main move loop already supports generated rooms when an exit points to an
  unknown room.
- `BlindAdventureRoomGenerator` and prompt/test coverage exist.
- The current Blind Adventure start flow still seeds a hardcoded `north` exit
  and does not yet feel like a clean end-to-end feature.
- That makes Blind Adventure a finishable stretch task, not a prerequisite for
  launching the core RPG.

### 2. Movement Language

Recommended V1 decision:

- Keep canonical movement commands in V1: directions plus `leave` / `exit`.
- If Blind Adventure is polished pre-launch, allow `forward`, `back`, `left`,
  and `right` there.
- Do not chase generic commands like `follow road` yet.

Why:

- `follow road` is attractive, but it needs exit labels, hints, or some other
  semantic exit model to work reliably.
- Without that model, the parser will guess too often and create player-facing
  confusion.
- `forward/back/left/right` is a good compromise for generated spaces because it
  feels less grid-like without requiring a whole new exit system.

### 3. Dynamic Stats

Recommended V1 decision:

- V1 keeps the canonical stat schema.
- DMs can tune stat values, caps, categories, semantic tags, and formulas in
  config/admin tooling.
- Full arbitrary stat trees are explicitly deferred.

Why:

- The config layer already points toward dynamic stats.
- The actual engine, player model, persistence layer, controller DTOs, level
  formulas, and world translation logic still depend heavily on
  `str/dex/con/int/wis/cha/luck`.
- Finishing arbitrary stats before launch would be a deep refactor across the
  whole stack, not a finishing pass.

Put differently:

- V1 supports configurable rules.
- V1 does not yet support arbitrary character schemas.

---

## Recommended Public V1 Positioning

If we were describing the product publicly today, the product should be framed as:

- a Discord-native AI-narrated RPG
- with real mechanics and persistent state
- with a web dashboard for operators/admins
- with one strong default world and a complete solo play loop

It should not be framed primarily as:

- a general-purpose world generator
- a CYOA engine
- a multi-world sandbox
- a fully schema-less tabletop rules engine

Those can still be the roadmap. They just should not define launch.

---

## Launch Checklist

### P0 - Must Be True Before Launch

- docs tell the truth about PostgreSQL, current architecture, and supported features
- README license text is internally consistent
- CI runs build/test on push
- setup guide works on a clean machine
- default world first-hour experience is polished
- user and admin smoke tests are repeatable
- narrator degraded mode is acceptable and understandable

### P1 - Strongly Recommended Before Launch

- remove or clearly mark stale/deprecated repo leftovers
- tighten the public README around one product story
- choose whether Blind Adventure is `experimental` or `shipping`
- add one short starter arc that showcases the best of the game

### P2 - Only If There Is Time

- finish Blind Adventure into a real feature
- add README images
- add extra dashboard polish beyond stability/readability

---

## Stretch Feature: Blind Adventure

If we choose to spend one more feature push before launch, this is the one to do.

Minimum acceptance criteria:

- `adventure start <id>` creates a real generated-space session
- the starting room uses `forward/back/left/right` language instead of a
  hardcoded `north` corridor
- moving inside Blind Adventure routes through `BlindAdventureRoomGenerator`
- room generation respects room caps and ends cleanly
- revisits, backlinks, and fallback rooms are stable
- player guidance explains the command vocabulary in that mode

Non-goals for this stretch:

- no `follow road` parser
- no arbitrary freeform exit semantics
- no attempt to make Blind Adventure replace the core RPG loop

If Blind Adventure cannot meet those criteria quickly, mark it experimental and
launch without it.

---

## Immediate Execution Order

1. Truth pass: README, AGENTS, setup docs, and launch positioning.
2. CI and build confidence.
3. Polish the default-world first-hour experience.
4. Decide Blind Adventure: ship cleanly or mark experimental.
5. Launch.

---

## Non-Negotiable Anti-Scope-Creep Rule

Before adding a new feature, ask:

"Does this make the shipped game more reliable, more understandable, or more fun
in the first hour?"

If the answer is no, it is not V1 work.
