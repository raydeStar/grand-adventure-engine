# Known Gaps & Future Work

Identified during the Thornwall demo world playthrough. None are blockers for the demo, but all would improve the experience for a real release.

---

## 1. No Buy/Sell System (IMPLEMENTED)

**Current behavior:** Items sit on the ground in shops. Players `take` them for free. NPCs like Korga and Pip have personality text about being merchants, but there's no mechanical transaction.

**Impact:** Low for demo (everything is OP loot anyway). High for a real game ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â economy matters.

**What's needed:**
- `ProcessBuyAsync` / `ProcessSellAsync` action handlers in GameEngine
- NPC `IsShopkeeper` flag with a `ShopInventory` separate from room items (so loot doesn't vanish when someone takes it)
- Gold deduction on buy, gold gain on sell (probably at reduced value)
- Price display when looking at shop items
- Haggle mechanic ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â CHA persuade check to lower prices (already have the social check infrastructure)
- Pip's "shameless haggler" personality should make haggling harder, Korga's "will give discounts to people who impress her" should make it easier

---

## 2. Potions Are Narrator-Driven (IMPLEMENTED)

**Current behavior:** `use potion` falls through to free-form AI processing. The narrator *should* read the item's `Effect` field ("Restores 30 HP") and return `statChanges: { "hp": 30 }`, but this depends entirely on the LLM understanding the item's effect string and doing the right thing. Small models frequently get this wrong.

**Impact:** Medium. Players may use a potion and not get healed, or get healed for the wrong amount. Especially bad mid-combat.

**What's needed:**
- Dedicated `ProcessUseAsync` handler that intercepts consumable items
- Parse the `Effect` field mechanically (regex patterns like `Restores (\d+) HP`)
- Apply HP/MP changes directly in the engine, not through the narrator
- Remove the item from inventory (decrement quantity)
- Pass the mechanical result to the narrator for flavor text only
- Fallback: if the effect string can't be parsed, fall through to narrator as today

---

## 3. Combat Is 1v1 Only (IMPLEMENTED)

**Current behavior:** `attack <target>` targets one NPC. If a room has Vex AND a Cultist, you fight them one at a time. The other hostile NPCs just... wait their turn politely.

**Impact:** Medium. Breaks immersion in multi-enemy rooms. The cultist shrine fight should feel like a dangerous ambush, not a queue.

**What's needed:**
- Initiative system using existing `CombatState` model (already has `TurnOrder` list and `CombatParticipant`)
- All hostile NPCs in the room join combat when any one is attacked
- Turn-based combat: player turn, then each enemy turn (using `CurrentTurnIndex` cycling)
- Enemy AI: each NPC attacks the player on their turn using their own `AttackBonus` / `DamageDice`
- Player can choose which enemy to target each turn
- When an enemy dies, remove from turn order and continue
- Fleeing should still work (escape the whole encounter)

**Existing infrastructure:** `CombatState`, `CombatParticipant`, `CombatPhase`, and `InitiativeFormula` are all defined in models and game-rules.yaml ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â just not wired up.

---

## 4. Take + Equip Is Two Commands (IMPLEMENTED)

**Current behavior:** `take thunderstrike blade` adds to inventory. `equip thunderstrike blade` equips from inventory. Two separate actions.

**Impact:** Low. Standard RPG flow, just slightly clunky for a text adventure where typing speed matters.

**What's needed:**
- Option A: Auto-equip on take if the slot is empty and the item is equippable (IMPLEMENTED)
- Option B: Support compound command `take and equip thunderstrike blade`
- Option C: `CommandParser` recognizes `grab` / `wield` / `don` as take-and-equip variants
- Probably Option A is best ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â auto-equip when the slot is empty, prompt "You found X. Equip it? (currently using Y)" when the slot is occupied

---

## 5. Shield/Helmet Defense (FIXED)

**Was:** Defense formula only included Armor, ignoring Shield and Helmet `ArmorValue`.

**Status:** Fixed in commit `61e9b89`. Defense now includes all three equipment slots.

---

## Priority Order

| Gap | Effort | Impact | Priority |
|-----|--------|--------|----------|
| Mechanical potion use | Small | High (combat reliability) | **P1** Ã¢â‚¬â€ DONE |
| Multi-enemy combat | Large | High (immersion) | **P2** Ã¢â‚¬â€ DONE |
| Buy/sell system | Medium | Medium (economy) | **P3** Ã¢â‚¬â€ DONE |
| Auto-equip on take | Small | Low (QoL) | **P4** Ã¢â‚¬â€ DONE |
