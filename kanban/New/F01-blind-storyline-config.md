# F01 — Blind Adventure: Storyline Context Object

**Category:** Feature — Blind Adventure Mode
**Effort:** Small-Medium
**Touches:** `GAE.Core/` models, config/YAML

## What

Create the data model that holds a "storyline" — the theme, setting, tone, and key plot beats that the narrator uses to generate rooms on the fly. This is the seed that keeps a blind adventure coherent.

## Why First

Everything else in Blind Adventure mode depends on this. The room generator needs to know what kind of rooms to create. The narrator needs tone and setting context. Without this, generated rooms are random nonsense.

## Design

```yaml
# Example: storyline definition
id: haunted-manor
name: "The Haunting of Ashwood Manor"
setting: "A crumbling Victorian manor on a fog-shrouded hilltop"
tone: "Gothic horror with dark humor"
theme: "Uncovering family secrets"
plot_beats:
  - "The front door locks behind you"
  - "You find a journal hinting at something in the basement"
  - "The ghost of Lady Ashwood appears and offers a deal"
  - "The final truth is in the attic"
starting_room_description: "The grand foyer — dusty chandeliers, portraits with eyes that follow you"
max_rooms: 20
```

## Acceptance Criteria

- [ ] `StorylineContext` model in GAE.Core with: Id, Name, Setting, Tone, Theme, PlotBeats (ordered list), StartingRoomDescription, MaxRooms
- [ ] Storyline can be loaded from YAML config file
- [ ] At least 2 example storyline YAML files (different genres — e.g., horror + fantasy heist)
- [ ] Unit test: storyline loads from YAML and all fields populated
- [ ] Model is clean and minimal — don't over-engineer this, it's narrator input

## Notes

- Plot beats are ordered but the narrator decides *when* to introduce them based on player progression.
- `max_rooms` is a soft cap — the narrator should steer toward the ending as you approach it.
