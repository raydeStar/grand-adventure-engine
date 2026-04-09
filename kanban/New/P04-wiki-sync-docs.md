# P04 — Document Wiki Sync Limitations

**Category:** Polish
**Effort:** Tiny
**Touches:** `docs/`, `README.md`

## What

WikiSync currently only writes data *out* to Wiki.js — it never reads it back in. This means editing the wiki doesn't change the game. Someone will definitely try to edit an NPC in the wiki and expect it to work.

## Why

This isn't a code fix for MVP — making wiki read-back work is a post-MVP feature. But it *is* a documentation fix. Tell people what the wiki does and doesn't do so they don't waste time.

## Acceptance Criteria

- [ ] A note in the README or setup guide explaining: "The wiki is currently a read-only mirror of game state. Editing the wiki directly will not change the running game."
- [ ] If there's a wiki section in any docs, add the same note there
- [ ] Optionally: a brief explanation of what *would* need to change to make wiki→engine sync work (for future you or contributors)

## Notes

- The wiki is still useful as a browsable reference for world state. Just set expectations.
