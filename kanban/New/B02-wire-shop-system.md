# B02 — Wire Up the Shop/Buy System

**Category:** Blocker
**Effort:** Medium-Large
**Touches:** `GAE.Engine/GameEngine.cs`, NPC shopkeeper logic, inventory

## What

`ProcessBuyAsync` exists in the engine but shop inventory isn't actually wired up. Players can talk to shopkeepers but can't buy or sell anything. This is a core gameplay loop — earn gold, buy better gear.

## Why This Blocks MVP

Gold is meaningless if there's nothing to spend it on. Players earn gold from quests and combat but hit a dead end when they try to trade. The shopkeeper NPC flag (`IsShopkeeper`) exists but leads nowhere functional.

## Acceptance Criteria

- [ ] Players can `!buy <item>` from a shopkeeper NPC
- [ ] Shopkeepers have a visible inventory (shown when you `!talk` to them or `!look` at their shop)
- [ ] Gold is deducted on purchase, item added to player inventory
- [ ] Players can `!sell <item>` to get gold back (at reduced price or configurable ratio)
- [ ] Shopkeeper inventory is defined per-NPC (in room/NPC config)
- [ ] Prices visible to the player before buying
- [ ] Edge cases handled: not enough gold, item not in stock, inventory full (if applicable)
- [ ] Tests covering buy, sell, insufficient gold, and item-not-found scenarios

## Notes

- Check `ProcessBuyAsync` and `ProcessTalkInternalAsync` in GameEngine.cs for existing scaffolding.
- Shopkeeper inventory could be a simple list on the NPC definition, or pulled from a config/YAML.
- Sell price = buy price * ratio (e.g., 0.5) is the simplest approach.
