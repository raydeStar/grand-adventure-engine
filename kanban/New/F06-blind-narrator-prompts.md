# F06 — Blind Adventure: Narrator Prompt Templates

**Category:** Feature — Blind Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Narrator/`, prompt templates

## What

The narrator needs specialized prompts for blind adventure mode. These are different from normal room narration — they need to incorporate the storyline, maintain tone consistency, and return structured room data the engine can parse.

## Depends On

- **F01** (Storyline Context) — prompts reference storyline fields
- **F02** (Dynamic Room Generator) — prompts serve the generation flow

## Prompts Needed

### 1. Room Generation Prompt
- Input: current room, direction, storyline context, visited rooms summary, next plot beat (if any), rooms remaining
- Output: structured data — room name, description, exits, optional NPCs, optional items
- Tone: must match `StorylineContext.Tone`

### 2. Plot Beat Integration Prompt
- Input: current scene, next plot beat to deliver
- Output: narration that weaves the beat into the room organically (not "suddenly, a journal appears")

### 3. Adventure Conclusion Prompt
- Input: storyline context, rooms visited, key events
- Output: wrap-up narration, adventure summary

## Acceptance Criteria

- [ ] Prompt templates created and stored consistently with existing narrator prompts
- [ ] Room generation prompt returns parseable structured output (JSON preferred)
- [ ] Prompts tested with at least 2 different storyline tones (horror, fantasy)
- [ ] Fallback text for each prompt if narrator fails
- [ ] Prompts don't leak engine internals to the narrator (no room IDs, no stat blocks in the prompt)

## Notes

- Narrator prompt quality directly determines how good blind adventures feel. Spend time on these.
- Test with your actual LM Studio model — prompt engineering varies by model.
