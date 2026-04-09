# How to Play Grand Adventure Engine

Welcome, adventurer. This guide covers everything you need to know to explore, fight, trade, and quest your way through the game.

---

## Your First 5 Minutes

1. **Create your character.** In Discord, type `/create`. The game will ask you who you want to be — describe yourself naturally: *"A sneaky goblin alchemist"* or *"A noble paladin with a dark secret."* The engine builds your full character from there.

2. **Look around.** Type `look` to see where you are. You'll get a description of the room, who's here, what exits are available, and any items on the ground.

3. **Move.** Type a direction to walk: `north`, `south`, `east`, `west` (or shorthand `n`, `s`, `e`, `w`). Diagonal and vertical movement works too: `ne`, `nw`, `se`, `sw`, `up`, `down`.

4. **Talk to someone.** If there's an NPC in the room, type `talk to <name>` to start a conversation. They'll respond based on their personality and how they feel about you.

5. **Do something unexpected.** The game understands natural language. Try *"search the bookshelf"*, *"kick the door down"*, or *"flex at the bartender."* The AI narrator handles anything that isn't a standard command.

---

## Commands at a Glance

You don't need to memorize all of these. The game accepts natural language for almost everything. These are shortcuts for common actions.

### Moving Around

| Command | What It Does |
|---------|-------------|
| `north` / `n` | Move north (also: `s`, `e`, `w`, `ne`, `nw`, `se`, `sw`, `up`, `down`) |
| `go north` | Same as above, with the `go` prefix |
| `look` | See the current room, NPCs, items, and exits |
| `look at <target>` | Examine something or someone specific |
| `map` | Show an ASCII map of explored rooms |

### Fighting

| Command | What It Does |
|---------|-------------|
| `attack <target>` | Start or continue a fight |
| `attack` | Attack the current combat target |
| `power attack` | Hit harder, but less accurate (-3 hit, +5 damage) |
| `aimed strike` | Attack with advantage (roll twice, take the better result) |
| `defend` | Raise your guard (bonus to defense this round) |
| `cast <spell>` | Cast a spell from your spellbook |
| `cast <spell> at <target>` | Cast a spell on a specific target |
| `flee` | Try to escape combat (enemies get a free hit) |
| `use <item>` | Use an item during combat (healing potions, etc.) |

### Talking to NPCs

| Command | What It Does |
|---------|-------------|
| `talk to <name>` | Start a conversation |
| *(any text while talking)* | Continue the conversation naturally |
| `goodbye` / `leave` | End the conversation |

NPCs remember how you've treated them. Be rude and they'll like you less. Help them and they'll warm up. Some are shopkeepers — talking to them opens trading mode.

### Inventory & Equipment

| Command | What It Does |
|---------|-------------|
| `inventory` / `i` | See what you're carrying |
| `take <item>` | Pick up an item from the room |
| `drop <item>` | Drop an item from your inventory |
| `equip <item>` | Wear or wield an item |
| `unequip <item>` | Remove an equipped item |
| `use <item>` | Use a consumable (potion, scroll, food) |
| `stats` | View your full character sheet |

### Shopping

| Command | What It Does |
|---------|-------------|
| `shop` / `wares` | See what the shopkeeper has for sale |
| `buy <item>` | Purchase an item (gold deducted, item added to inventory) |
| `sell <item>` | Sell an item for half its value |

You need to be in a room with a shopkeeper. Talk to them first, or just use `shop` to browse their wares.

### Quests

| Command | What It Does |
|---------|-------------|
| `journal` / `quests` | See your active quests |
| `completed` | See finished quests |
| `quest <name>` | Get details on a specific quest |
| `accept quest <name>` | Accept a quest from an NPC |
| `turn in <name>` | Complete a quest (talk to the quest giver) |
| `abandon quest <name>` | Give up on a quest |

### Magic

| Command | What It Does |
|---------|-------------|
| `spellbook` | See your known spells |
| `cast <spell>` | Cast a spell (costs MP) |
| `cast <spell> at <target>` | Cast a spell on a specific target |

### Resting

| Command | What It Does |
|---------|-------------|
| `rest` | Take a short rest (partial HP/MP recovery) |
| `camp` / `long rest` | Take a long rest (full HP/MP recovery, takes more time) |

### Other

| Command | What It Does |
|---------|-------------|
| `help` | Quick command reference |
| `hint` | Get a suggestion for what to do next |
| `lorebook` | Browse world lore you've discovered |
| `narrator` | See available narrator voices |
| `narrator <name>` | Switch narrator style |

---

## How Combat Works

Combat is turn-based. When you attack an enemy (or one attacks you), you enter combat mode.

**Each round:**
1. You choose an action: `attack`, `power attack`, `aimed strike`, `defend`, `cast`, `use`, or `flee`.
2. Dice are rolled behind the scenes. Your attack bonus (from stats and gear) is added to a d20 roll and compared against the enemy's defense.
3. If you hit, damage is rolled based on your weapon.
4. Then the enemies get their turn and attack you the same way.
5. Repeat until one side falls — or you flee.

**Special moves:**
- **Power Attack** trades accuracy for damage. Good against slow, heavily-armored targets.
- **Aimed Strike** rolls your attack twice and takes the better result. Good for landing a hit on evasive targets.
- **Defend** boosts your defense until your next turn. Use it when you're low on HP and waiting for the right moment.

**Critical hits** (natural 20) deal double damage. **Fumbles** (natural 1) are an automatic miss.

---

## Death & Respawn

If your HP hits zero, you die — but it's not the end. You respawn at the tavern (or the nearest safe location) with full HP and MP. Your gold, inventory, and experience are all intact.

The enemy that killed you remembers the fight. You can go back for revenge whenever you're ready.

---

## Leveling Up

You earn XP from defeating enemies and completing quests. When you have enough, you automatically level up:

- **Level 1→2** costs 100 XP. Each level costs more: level 5→6 costs 500 XP.
- **HP and MP increase** each level based on your Constitution and Intelligence.
- **You're fully healed** on level-up.
- **Max level is 20.**

Check your progress with `stats`.

---

## Tips

- **Talk to everyone.** NPCs give quests, sell gear, and share lore. Some have information that helps you elsewhere.
- **Type naturally.** You don't have to use exact commands. *"I run behind the barrel and hide"* works just as well as `hide`.
- **Read the room.** The narrator describes the environment for a reason. Exits, items, and NPCs mentioned in the narration are all interactable.
- **Check your journal.** If you're not sure what to do, type `journal` to see active quests, or `hint` for a suggestion.
- **Save your gold.** Shops sell better gear as you level up. Don't blow everything on the first sword you see.
- **Retreat is valid.** If a fight is going badly, `flee` and come back stronger. Death isn't catastrophic, but it's a trek back.
