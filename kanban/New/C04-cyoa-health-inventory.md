# C04 — CYOA: Health & Inventory Mechanics

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Small-Medium
**Touches:** `GAE.Engine/`, narrator prompts

## What

CYOA mode has simplified health (Healthy → Hurt → Critical → Dead) and a flat inventory (list of named items). The narrator drives both — telling the engine when health changes or items are gained/lost.

## Depends On

- **C01** (Game Mode & Simplified State)
- **C02** (Choice Tree) — health/inventory change as part of choice resolution

## Design

### Health
- 4 states: `Healthy`, `Hurt`, `Critical`, `Dead`
- Narrator signals health changes in structured output: `"health_change": "hurt"` or `"health_change": "worse"`
- Engine enforces transitions: Healthy→Hurt→Critical→Dead (can skip levels for dramatic moments)
- Healing is possible: narrator can improve health ("You find healing herbs")
- Health shown to player as flavor text, not numbers: "You're feeling battered but alive"

### Inventory
- Flat list of strings: `["Torch", "Rusty Key", "Mysterious Letter"]`
- Narrator signals: `"items_gained": ["Silver Dagger"]`, `"items_lost": ["Torch"]`
- Items can gate choices: "Use the Rusty Key to open the door" only appears if you have it
- No item limit for MVP (keep it simple)
- `!inventory` or `!inv` shows current items

### Narrator Responsibilities
- Narrator decides when health changes and items change
- Narrator sees current health and inventory in its prompt
- Narrator uses items to gate choices (only offers "use the key" if player has it)

## Acceptance Criteria

- [ ] Health enum with 4 states, transitions enforced by engine
- [ ] Health changes signaled by narrator, applied by engine
- [ ] `!health` or status shown with flavor text, not raw enum
- [ ] Inventory as string list, add/remove driven by narrator output
- [ ] `!inventory` shows current items
- [ ] Narrator prompt includes current health and inventory
- [ ] Choices can be gated by inventory items
- [ ] Test: health transitions (Healthy→Hurt→Critical→Dead)
- [ ] Test: item gain and loss
- [ ] Test: inventory-gated choice not shown when item missing

## Notes

- The narrator is trusted more in CYOA mode than in full RPG. In full RPG, the engine decides everything. In CYOA, the narrator drives health/inventory changes because there are no dice rolls or stat checks.
- Keep the engine as a guardrail: it validates transitions (can't go from Dead to Healthy in one step) but doesn't override the narrator's creative decisions.
