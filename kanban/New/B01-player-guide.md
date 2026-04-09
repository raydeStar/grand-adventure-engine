# B01 — Player Guide

**Category:** Blocker
**Effort:** Medium
**Touches:** `docs/`, Discord bot help command

## What

Write a player-facing guide that explains how to actually play the game. Right now, someone joining the Discord server has nothing beyond `/create` and a help embed listing commands.

## Why This Blocks MVP

Real players will join, type `/create`, finish character creation, and then have no idea what to do next. They won't know what `!look` does vs `!go`, how combat works, what happens when they die, or how quests work. You'll get questions in chat instead of gameplay.

## Acceptance Criteria

- [ ] A `docs/player-guide.md` file exists (or equivalent)
- [ ] Covers: getting started, movement commands, combat basics, talking to NPCs, inventory/equipment, quests, death/respawn
- [ ] Written in plain language (not dev jargon) — match the README tone
- [ ] Linked from the README under a "How to Play" section
- [ ] Consider adding a condensed version as the `/help` embed in Discord (or linking to the full guide)

## Notes

- The `/help` command already returns an embed with command names. This guide goes deeper — *when* and *why* to use each command, not just what they are.
- Could include a "your first 5 minutes" walkthrough scenario.
