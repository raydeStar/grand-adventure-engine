# Task Priorities

Work through these in order. Each group should be finished before moving to the next, but within a group you can jump around based on what you feel like tackling.

---

## Priority 1 — Blockers (fix before anyone plays)

These are bugs or gaps that would break the experience for real players.

| Order | Task | Effort | Why This Order |
|---|---|---|---|
| 1 | **B05** Narrator retry logic | Small | Quick win. Immediately improves reliability for everything else you test. |
| 2 | **B03** Death & respawn tests | Small-Med | Core loop. If death is broken, nothing else matters. Write these before touching combat. |
| 3 | **B04** Leveling & XP tests | Small-Med | Pairs with B03 — same test fixtures, same area of the engine. Knock both out together. |
| 4 | **B02** Wire up shop system | Med-Large | Biggest blocker. Gold is meaningless without shops. Do this after tests so you have a safety net. |
| 5 | **B06** Self-hosting setup guide | Medium | Write this while everything is fresh. You'll need it when you hand this to anyone else. |
| 6 | **B01** Player guide | Medium | Write last in this group — by then you'll have played through the full loop and know what to document. |

---

## Priority 2 — Polish (make it presentable)

These make the project look finished. Do them before sharing publicly.

| Order | Task | Effort | Why This Order |
|---|---|---|---|
| 7 | **P02** Choose a license | Tiny | 2-minute decision. Just do it. |
| ~~8~~ | ~~**P04** Wiki sync docs~~ | ~~Tiny~~ | ~~Obsolete — Wiki.js removed~~ |
| 9 | **P01** README images | Small | Needs a running game for screenshots. By now you'll have one. |
| 10 | **P03** Dashboard UI baseline | Med-Large | Biggest polish task. Tackle it last — functional dashboard is nice but not critical for Discord players. |

---

## Priority 3 — Blind Adventure Mode (dynamic rooms)

Your first new feature. Build these in dependency order.

| Order | Task | Effort | Why This Order |
|---|---|---|---|
| 11 | **F04** Evaluate DungeonGenerator | Small (spike) | Research first. Might change your approach to F02/F03. |
| 12 | **F01** Storyline context object | Small-Med | Foundation. Everything else references this. |
| 13 | **F06** Narrator prompt templates | Medium | Write prompts before the generator so you know what the narrator will return. |
| 14 | **F02** Dynamic room generator | Large | The core feature. Depends on F01 and F06. |
| 15 | **F03** Exit & connectivity logic | Medium | Refines F02 — handles the graph/wiring details. |
| 16 | **F05** Session management & guardrails | Medium | Wraps it all up — start, play, end lifecycle. |

---

## Priority 4 — Choose Your Own Adventure Mode (book mode)

Your second new feature. More self-contained than Blind Adventure.

| Order | Task | Effort | Why This Order |
|---|---|---|---|
| 17 | **C01** Game mode flag & simplified state | Medium | Foundation. Everything checks the mode flag. |
| 18 | **C04** Health & inventory mechanics | Small-Med | Simple, self-contained. Build the mechanics before the choice tree needs them. |
| 19 | **C02** Choice tree & branch generation | Large | The core feature. Depends on C01 and C04. |
| 20 | **C03** Save points & death rewind | Medium | Depends on C02 — needs the choice tree to exist. |
| 21 | **C05** Endgame & story conclusion | Small-Med | Depends on C02 and C03. Wraps the story. |
| 22 | **C06** Narrator prompt templates | Medium | Write these after you know what the engine needs (C01-C05). |
| 23 | **C07** Discord integration | Medium | Final integration. Wire everything into Discord. Do this last. |

---

## Priority 5 — Post-MVP (when you're ready)

None of these block launch. Tackle them based on player feedback.

| Task | Effort | Notes |
|---|---|---|
| **X01** Spell system tests & scaling | Medium | Do when players start using magic heavily |
| **X02** Party quest sync | Medium | Do when multiplayer quests are getting used |
| **X03** NPC disposition → combat | Small-Med | Cool flavor feature, not urgent |
| **X04** SignalR live dashboard | Med-Large | Do when you're actively monitoring games |
| ~~**X05** Wiki read-back~~ | ~~Large~~ | ~~Obsolete — Wiki.js removed~~ |
| **X06** Race & class abilities | Large | Do when players want more character depth |

---

## Task ID Reference

| ID | File | Status |
|---|---|---|
| D01 | `Completed/D01-readme-rewrite.md` | Done |
| B01 | `New/B01-player-guide.md` | New |
| B02 | `New/B02-wire-shop-system.md` | New |
| B03 | `New/B03-death-respawn-tests.md` | New |
| B04 | `New/B04-leveling-xp-tests.md` | New |
| B05 | `New/B05-narrator-retry.md` | New |
| B06 | `New/B06-setup-guide.md` | New |
| P01 | `New/P01-readme-images.md` | New |
| P02 | `New/P02-choose-license.md` | New |
| P03 | `New/P03-dashboard-ui.md` | New |
| P04 | `New/P04-wiki-sync-docs.md` | New |
| F01 | `New/F01-blind-storyline-config.md` | New |
| F02 | `New/F02-blind-dynamic-room-gen.md` | New |
| F03 | `New/F03-blind-exit-generation.md` | New |
| F04 | `New/F04-blind-dungeon-gen-eval.md` | New |
| F05 | `New/F05-blind-session-management.md` | New |
| F06 | `New/F06-blind-narrator-prompts.md` | New |
| C01 | `New/C01-cyoa-game-mode.md` | New |
| C02 | `New/C02-cyoa-choice-tree.md` | New |
| C03 | `New/C03-cyoa-save-points.md` | New |
| C04 | `New/C04-cyoa-health-inventory.md` | New |
| C05 | `New/C05-cyoa-endgame.md` | New |
| C06 | `New/C06-cyoa-narrator-prompts.md` | New |
| C07 | `New/C07-cyoa-discord-integration.md` | New |
| X01 | `New/X01-spell-system-tests.md` | New |
| X02 | `New/X02-party-quest-sync.md` | New |
| X03 | `New/X03-npc-disposition-combat.md` | New |
| X04 | `New/X04-signalr-live-updates.md` | New |
| X05 | `New/X05-wiki-read-back.md` | New |
| X06 | `New/X06-race-class-abilities.md` | New |
