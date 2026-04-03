# Discord Bot — Implementation Spec

This is the full spec for the Discord bot player experience. Everything in this document is a requirement, not a suggestion. Build all of it.

## Current State

- Discord.Net 3.x bot running as `IHostedService` in the ASP.NET Core app
- Prefix-based commands (`!create`, `!look`, `!go north`, etc.)
- 5-step rigid character creation wizard (name → race → class → stat method → backstory)
- Plain text + markdown + emoji formatting, no embeds
- No channel/thread management — responds wherever the command was typed
- Player ID = Discord user ID (string)
- Message chunking at 2000 chars
- World state is global/shared (rooms, NPCs, items)
- NPC disposition is global (not per-player)
- Death leaves the player at 0 HP with no recovery
- No victory handling when Goretusk dies

---

## Architecture Changes Required Before Discord Launch

### A. Per-Player Room Instances

The world is shared. If Player A takes the Thunderstrike Blade, it's gone for Player B. If Player A kills Goretusk, the boss room is empty for everyone. This breaks multiplayer completely.

**Requirement:** Each player gets their own copy of each room. NPCs, items, and combat state are all per-player. Players never see or affect each other's world state.

**Implementation:**

1. Rooms seeded from `lore-seed.yaml` are stored as **templates** (keyed as `"template:{room_id}"` or stored in a separate template collection).
2. Add `GetPlayerRoomAsync(string playerId, string roomId)` to `IStateManager`. On first call for a player+room combo, clone the template and save as the player's instance (`"{playerId}:{roomId}"`). On subsequent calls, return the player's copy.
3. All engine code that calls `GetRoomAsync(roomId)` during gameplay changes to `GetPlayerRoomAsync(playerId, roomId)`. Admin/overview endpoints keep using `GetRoomAsync` for templates.
4. `ProcessMoveAsync`, `ProcessTakeAsync`, `ProcessAttackAsync`, `ProcessTalkInternalAsync`, `ProcessLookAsync`, `ProcessDropAsync`, `ProcessFreeFormActionAsync` — all of these must use the player's room instance, not the global template.
5. World reset (`POST /api/dashboard/admin/reset-world`) deletes all player room instances. Templates are re-seeded from YAML.
6. Player restart deletes that player's room instances only.

**Storage:** ~13 rooms × N players. At 5 players = 65 room copies. Trivial for in-memory. Journal/checkpoint handles persistence.

### B. Per-Player NPC Disposition

NPC disposition (`NpcDispositionState`) currently lives on the `Npc` object inside the room. With per-player room instances (section A), this is automatically solved — each player's room copy has its own NPC instances with independent disposition. No additional model changes needed.

If for any reason instanced rooms are not implemented first, the fallback is:
- Change `Npc.DispositionState` from `NpcDispositionState` to `Dictionary<string, NpcDispositionState>` keyed by player ID.
- Add `Npc.GetDispositionFor(string playerId)` that returns the player-specific state or a fresh default.
- All engine code that reads/writes `npc.DispositionState` must pass the current player ID.

### C. Death → Tavern Respawn

When a player hits 0 HP, they are currently stuck at 0 HP with no recovery mechanic.

**Requirement:** When player HP hits 0, automatically respawn them at the tavern.

**Implementation:**

1. When `player.Hp <= 0` after combat damage (in both `ProcessAttackAsync` and the combat turn flow):
   - Set `player.Hp = player.MaxHp / 2` (respawn at 50% HP)
   - Set `player.Mp = player.MaxMp / 2` (50% MP)
   - Set `player.Gold = Math.Max(0, player.Gold - player.Gold / 4)` (lose 25% gold as death tax)
   - Set `player.CurrentRoomId = "spawn"`
   - Reset `player.Interaction` to explore mode
   - Keep inventory and equipment (dying does not delete gear)
   - Keep XP
2. The narration for death must include:
   - A dramatic defeat message from the narrator
   - A "You wake up in The Rusted Flagon" message
   - The gold penalty displayed: "💀 You lost X gold."
   - Current HP/MP status
3. The NPC that defeated the player gets memory flag `"defeated-player"` on that player's room instance.

### D. Victory Handling

When Goretusk (ID: `boss_goretusk`) is defeated, there is no special handling.

**Requirement:** Detect boss kill and fire a victory event.

**Implementation:**

1. After NPC death in combat, check if `enemy.Id == "boss_goretusk"` (or check NPC personality text for "VICTORY CONDITION").
2. Award bonus rewards: +200 XP, +500 gold (on top of normal loot).
3. Set a flag on the player: `player.HasCompletedDemo = true` (add this bool to `PlayerCharacter`).
4. Return a special `ActionResult` with a victory flag so the Discord bot can format it differently.
5. In Discord: post a victory announcement to the main channel (see section 5 below).
6. Player can keep playing after victory — explore more, restart, whatever. The game doesn't end.

---

## Discord UX Spec

### 1. Thread Per Player

Each player gets their own private Discord thread. This thread is their game window.

**Structure:**
```
#gae-tavern (main channel — lobby, announcements only)
  ├── ⚔️ Grok's Adventure (private thread — Grok's game)
  ├── ⚔️ Elara's Adventure (private thread — Elara's game)
  └── ⚔️ Bongo's Adventure (private thread — Bongo's game)
```

**Rules:**
- Thread is created when the player types `/create` or `!create` in the main channel.
- Thread type: `ThreadType.PrivateThread`.
- Thread name: `⚔️ {CharacterName}'s Adventure` (set after character creation completes, initially `⚔️ {DiscordUsername}'s Adventure`).
- Bot only processes game commands from the thread owner inside their thread. Other users' messages in the thread are ignored.
- Game commands typed in the main channel get a reply: `"Head to your adventure thread to play! If you don't have one, type /create."` with a link to their thread.
- Thread auto-archives after 24h of inactivity (Discord default). Bot unarchives the thread when the player sends their next message.
- Store `ThreadId` (ulong) on `PlayerCharacter` so the bot can find/reopen threads after restart.

### 2. Character Creation — AI-Driven Conversation

Replace the rigid 5-step wizard with a natural conversation.

**Flow:**

1. Player types `/create` or `!create` in the main channel.
2. Bot creates a private thread.
3. Bot posts the opening message:
   ```
   Welcome, traveler. I am Sir Thaddeus, keeper of fates.
   Tell me about yourself. Who are you? What are you?
   Speak freely — I'll shape your destiny from your words.

   (Describe yourself however you like: "I'm a sneaky halfling who
   picks pockets" or "I'm a massive orc who solves problems with fists"
   — or just tell me your name and I'll ask questions.)
   ```
4. Player responds naturally. Examples: "I'm a big strong ox of a man, not too bright", "elf mage named Elara", "I want to be a sneaky thief".
5. Bot sends the description to the narrator with a character creation system prompt (see Technical Notes below). The AI returns a JSON character concept: race, class, stat allocation (from standard array), backstory suggestion, and optionally a follow-up question.
6. Bot presents the sheet as a formatted embed:
   ```
   ⚔️ CHARACTER SHEET
   Name: ???
   Race: Human   Class: Barbarian
   STR: 15 (+2)  DEX: 13 (+1)
   CON: 14 (+2)  INT:  8 (-1)
   WIS: 12 (+1)  CHA: 10 (+0)
   HP: 22/22  MP: 4/4  Gold: 50

   "A hulking brute who lets his fists do the talking..."

   How does this look? You can say:
   • "Make me stronger" / "I want more charisma"
   • "Change my class to Fighter"
   • "My name is Grok"
   • "Looks good" / "I'm done, let's go"
   ```
7. Player iterates. Each response goes back to the AI, which adjusts the sheet. Bot re-presents the updated embed each time.
8. Player finalizes with a confirming phrase ("looks good", "done", "let's go", "perfect", etc.).
9. Bot calls `CreateCharacterFromConceptAsync` with the final stats.
10. Bot renames the thread to `⚔️ {CharacterName}'s Adventure`.
11. Bot posts the starting room description (The Rusted Flagon).

**Guardrails:**
- Stats always use the standard array [15, 14, 13, 12, 10, 8]. The AI decides assignment order, not arbitrary numbers. Engine validates on creation.
- Race must be one of: Human, Elf, Dwarf, Halfling, Orc, Tiefling.
- Class must be one of: Fighter, Mage, Rogue, Cleric, Ranger, Bard.
- If AI returns invalid values, fall back to defaults.
- If narrator is down, fall back to the existing rigid wizard (keep it as a fallback path, don't delete it).

**AI Prompt for Character Creation:**
```
System: You are a character creation assistant for a D&D-style text adventure.
The player will describe their character in natural language. Based on their
description, generate a character sheet.

You MUST assign stats from the standard array [15, 14, 13, 12, 10, 8].
Order them based on the player's description (strong character → STR gets 15, etc.)

Valid races: Human, Elf, Dwarf, Halfling, Orc, Tiefling
Valid classes: Fighter, Mage, Rogue, Cleric, Ranger, Bard

Return JSON:
{
  "name": "suggested name or null if player didn't say one",
  "race": "Human",
  "class": "Fighter",
  "statOrder": ["str", "con", "dex", "wis", "cha", "int"],
  "backstory": "2-3 sentence backstory based on their description",
  "followUpQuestion": "optional question if the description was vague, or null"
}

If the player asks to adjust stats, re-order the standard array accordingly.
If the player specifies a name, use it. If they change their mind, update it.
```

### 3. Message Formatting

**Use Discord embeds for structured data:**
- Character sheet (`!stats`)
- Inventory and equipment (`!inv`)
- Room entry header (name, NPCs, items, exits)
- Combat results (attack rolls, damage, loot)
- Victory screen
- Map

**Use plain text for narrative content:**
- Narration / story text
- Conversation dialogue
- Free-form action results

**Room Entry Format (embed + narration below):**
```
[EMBED — title: "📍 Korga's Forge", color: orange]
Fields:
  👤 NPCs: Korga Ironjaw
  📦 Items: Thunderstrike Blade, Ironbark Plate Armor, Starfall Shield
  🚪 Exits: ← south (Town Square)
Footer: ❤️ 21/21  ✨ 4/4  💰 50g

[Plain text below embed:]
Korga looks up from her anvil and grins. "Another adventurer
looking to get themselves killed. At least pick up something
sharp first."
```

**Combat Result Format:**
```
⚔️ COMBAT — Hollow Skeleton
🎲 Attack Roll: [17] + 2 = 19 vs AC 12 — HIT!
🎲 Damage: [6, 4, 7] + 5 = 22

The skeleton crumbles to dust under your thunderous blow.

💀 Hollow Skeleton defeated!
⭐ +20 XP  💰 +6 gold
🎁 Loot: Rusted Longsword

❤️ 21/21  ✨ 4/4  💰 56g
```

**Every response includes the HP/MP/Gold status bar** as the embed footer or as a plain text line at the bottom. The player never has to type `!stats` just to see their HP.

**Mobile constraint:** Keep narration under 500 characters. Add a `discord` mode flag to narrator prompts that enforces shorter output. Room revisits get abbreviated descriptions: "You return to Korga's Forge. The heat hits you again." (first visit gets full description, return visits get a one-liner).

### 4. Commands

**Slash commands (registered with Discord for discoverability):**

| Command | Description |
|---------|-------------|
| `/create` | Start character creation |
| `/restart` | Reset character and start over |
| `/stats` | View character sheet |
| `/inventory` | View inventory and equipment |
| `/help` | Show all commands |
| `/map` | Show discovered rooms as ASCII map |

**Prefix commands (for speed during gameplay):**

| Command | Aliases | Description |
|---------|---------|-------------|
| `!go <direction>` | `!north`, `!n`, `!south`, `!s`, etc. | Move |
| `!look` | `!l` | Look at surroundings |
| `!look at <target>` | | Examine something specific |
| `!attack <target>` | `!a <target>` | Attack an NPC |
| `!talk to <target>` | `!t <target>` | Start conversation |
| `!take <item>` | `!grab <item>` | Pick up an item |
| `!drop <item>` | | Drop an item |
| `!equip <item>` | `!eq <item>` | Equip from inventory |
| `!unequip <item>` | | Unequip to inventory |
| `!use <item>` | | Use a consumable |
| `!rest` | `!short rest`, `!long rest` | Rest and recover |
| `!inv` | `!i`, `!inventory` | View inventory |
| `!stats` | `!st` | View character sheet |
| `!map` | `!m` | Show discovered rooms |
| `!help` | `!h` | Show commands |
| `!restart` | | Reset character |

**Conversation mode — no prefix required:**

When the player is in `InteractionMode.Conversation` or `InteractionMode.Combat`, all messages in their thread go directly to the game without needing a `!` prefix. The player just types naturally:

```
Player: !talk to mara
Bot:    Mara looks up from polishing a mug. "Well hello there..."

Player: tell me about the dungeon        ← no prefix
Bot:    Mara leans in close...

Player: flirt with her                    ← no prefix, triggers social check
Bot:    🎲 Charm (CHA +0): [14] vs DC 7 — SUCCESS
        Mara's cheeks flush...

Player: !leave                            ← prefix to exit conversation
Bot:    You step away from the bar.
```

Exit keywords that always work (with or without prefix): `leave`, `bye`, `walk away`, `stop talking`, any `go <direction>` command.

### 5. Victory Announcement

When a player kills Goretusk, post in the main channel:

```
🏆 **{CharacterName} has defeated Goretusk the Undying!**
The demon-boar of the Dread Hollow has been slain.
Thornwall is safe... for now.
```

Maintain a **Hall of Heroes** section in the main channel pinned message. Update it when a player completes the demo. Show character name, race, class, and completion time.

### 6. Restart Flow

Player types `!restart` or `/restart` in their thread:

1. Bot posts: `"Are you sure? Your character will be wiped — stats, items, gold, progress. NPCs will forget you. Type 'yes' to confirm."`
2. Player types `yes`.
3. Bot resets the player:
   - Delete all per-player room instances for this player
   - Clear player inventory, equipment, gold, XP
   - Reset to level 1 at spawn
   - Clear story entries for this player
4. Bot re-enters character creation flow in the same thread.
5. Thread is renamed after the new character is created.

### 7. Map Command

`!map` generates an ASCII map of rooms the player has visited:

```
📍 YOUR MAP (discovered rooms)

                    [Fairy Grove]
                         |
[Blacksmith]---[Town Square]---[Gate]---[Forest Edge]---[Deep Forest]
                    |                                        |
               [Gen. Store]                          [Dungeon Entrance]
                                                          |
  [Back Alley]---[⭐ Tavern]                        [Dungeon Hall]---[Shrine]
                                                          |
                                                    [??? Depths]

⭐ = You are here
??? = Unexplored (you know it exists but haven't visited)
```

Generate from room exit data. Only show rooms the player has visited (`IsDiscovered` in their room instance). Show connected-but-unvisited rooms as `???`.

### 8. Onboarding

**Main channel pinned message:**

```
🎮 WELCOME TO THE GRAND ADVENTURE ENGINE

You're standing at the entrance to Thornwall, a frontier town
on the edge of the Dread Hollow. Something terrible stirs in
the dungeon below. Will you stop it?

👉 Type /create to begin your adventure
👉 Each player gets their own private thread
👉 Type !help in your thread for commands

⚔️ GOAL: Gear up in town, brave the Whisperwood, and defeat
         Goretusk the Undying in the Dread Hollow.

🏆 HALL OF HEROES:
  (no one yet — will you be the first?)
```

When a player types `/create` in the main channel:
- If they have no character: create thread, start character creation.
- If they already have a character: reply with a link to their existing thread.

When a player types a game command in the main channel:
- Reply: `"Head to your adventure thread to play!"` with a thread link.

### 9. Narrator Timeout & Fallback

When LM Studio is down or slow:

1. All narrator HTTP calls have a **10-second timeout**. Do not make the player wait longer.
2. On timeout or error, use mechanical fallback text:
   - `look`: "You look around. You see: [NPC list]. Items: [item list]. Exits: [exit list]."
   - `talk`: "The NPC regards you but says nothing." (conversation still enters conversation mode)
   - Free-form: "[Free action: {input}] The narrator is unavailable — action noted but not narrated."
   - Combat narration: skip narration, show only the mechanical dice results
3. Mechanical commands (move, take, drop, equip, stats, inventory, help, map) do not need the narrator and always work instantly.
4. If the narrator has failed 3+ times in 5 minutes, post a one-time warning in the player's thread: `"⚠️ The storyteller is resting. Commands still work, but descriptions will be brief until it recovers."`
5. When the narrator recovers, post: `"✨ The storyteller has returned."`
6. In the admin channel, log narrator up/down events.

### 10. Admin / DM Commands

All admin commands go in a hidden `#dm-room` channel. Bot processes these with admin auth.

| Command | Effect |
|---------|--------|
| `!dm heal @player` | Set player to full HP/MP |
| `!dm teleport @player <room_id>` | Move player to a room |
| `!dm grant @player <item_name>` | Give player an item (use grant-item API) |
| `!dm say <room_id> "message"` | Inject a DM narration into the thread of any player currently in that room |
| `!dm kill <npc_id> <player>` | Remove an NPC from a player's room instance |
| `!dm spawn <npc_name> <room_id> <player>` | Add an NPC to a player's room instance |
| `!dm reset-world` | Nuke everything and reseed (requires `yes` confirmation) |
| `!dm status` | Show all players: name, location, HP, thread status |
| `!dm announce "message"` | Post a message to the main channel as the bot |

The bot posts automatic events to `#dm-room`:
- Player created a character
- Player died
- Player defeated a boss
- Player restarted
- Narrator went down / came back

### 11. Session Continuity

**Player returns after being AFK:**
- Thread is auto-archived by Discord after 24h inactivity.
- Bot unarchives the thread when the player sends any message.
- Bot posts a context message: `"You find yourself in {room_name}. {brief room description}. ❤️ {hp}/{maxHp} ✨ {mp}/{maxMp} 💰 {gold}g"`
- NPC dispositions have already decayed toward baseline via the 1-hour half-life system.

**Bot restarts:**
- State checkpointed on shutdown, replayed from journal on startup (already implemented).
- Threads persist in Discord.
- `PlayerCharacter.ThreadId` (ulong, stored as string) maps players back to their threads.
- On startup, bot does NOT need to scan threads. It discovers the thread ID on the player's next message and updates the stored value if it changed.

---

## Implementation Order

Build in this order. Items 1-6 are required before launch with multiple players. Items 7-11 are required for a polished experience.

| # | Task | Description |
|---|------|-------------|
| 1 | **Per-player room instances** | Add `GetPlayerRoomAsync`, clone rooms on first visit, update all engine methods to use player rooms |
| 2 | **Death → tavern respawn** | Detect 0 HP, respawn at spawn with 50% HP/MP, 75% gold, keep gear |
| 3 | **Thread per player** | Create private thread on `/create`, store `ThreadId` on player, route commands to thread owner only |
| 4 | **Room entry formatting** | Embed with room name/NPCs/items/exits + narration text + status bar footer |
| 5 | **Conversation mode (no prefix)** | In Conversation/Combat mode, all thread messages go to game without `!` prefix |
| 6 | **AI character creation** | Replace rigid wizard with narrator-driven conversation, iterate until player confirms |
| 7 | **Victory handling** | Detect Goretusk kill, bonus rewards, main channel announcement, Hall of Heroes |
| 8 | **Restart flow** | Delete player room instances, reset player, re-enter character creation |
| 9 | **Narrator timeout/fallback** | 10s timeout, mechanical fallback text, narrator health notifications |
| 10 | **Admin DM commands** | Hidden #dm-room channel with heal/teleport/grant/say/status commands |
| 11 | **Map command** | ASCII map from discovered room data |
| 12 | **Slash command registration** | Register /create, /restart, /stats, /inventory, /help, /map with Discord |
