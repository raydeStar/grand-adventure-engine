# C06 — CYOA: Narrator Prompt Templates

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Medium
**Touches:** `GAE.Narrator/`, prompt templates

## What

CYOA mode needs its own narrator prompts, distinct from full RPG narration. The narrator is doing more work here — it's driving the story, generating choices, signaling health/inventory changes, and deciding when the story ends.

## Depends On

- **C01** through **C05** — prompts serve all CYOA mechanics

## Prompts Needed

### 1. Story Opening Prompt
- Input: story theme/setting (could be player-described or pre-configured), player name
- Output: opening narration + first set of choices
- Sets the tone for the entire adventure

### 2. Choice Resolution Prompt
- Input: the choice made, current health, inventory, recent history (last 3-5 nodes), story context
- Output: structured response with:
  - `narration` (the next scene)
  - `choices` (2-4 options, optionally gated by inventory)
  - `health_change` (optional — null if unchanged)
  - `items_gained` / `items_lost` (optional)
  - `is_save_point` (boolean)
  - `ending` (optional — null if story continues)

### 3. Death Narration Prompt
- Input: what killed the player, current scene
- Output: dramatic death description + transition to rewind

### 4. Ending Narration Prompt
- Input: ending type, full adventure summary data
- Output: conclusion scene + epilogue

## Acceptance Criteria

- [ ] All 4 prompt templates created
- [ ] Choice resolution returns parseable structured output (JSON)
- [ ] Prompts include inventory and health context so narrator can gate choices
- [ ] Prompts maintain tone consistency across the adventure
- [ ] Fallback for each prompt if narrator fails
- [ ] Tested with actual LM Studio model for quality
- [ ] Prompts don't exceed reasonable token counts (watch context window)

## Notes

- The choice resolution prompt is the workhorse — it runs every turn. Keep it tight.
- Include a "story so far" summary in each prompt rather than full history. Summarize every 5-10 nodes to keep token count manageable.
- Structured output format must be reliable. Consider giving the model a JSON schema example in the prompt.
