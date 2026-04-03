# Discord Bot — Full Player Experience Plan

## What Exists Today

- Discord.Net 3.x bot running as `IHostedService`
- Prefix-based commands (`!create`, `!look`, `!go north`, etc.)
- 5-step rigid character creation wizard (name → race → class → stat method → backstory)
- Plain text + markdown + emoji formatting, no embeds
- No channel/thread management — responds wherever the command was typed
- Player ID = Discord user ID (string)
- Message chunking at 2000 chars

## What We Want

A polished Discord-native text adventure where players create characters through natural conversation, explore the world in their own threads, and have a clean UX that doesn't require reading a manual.

---

## 1. Character Creation — AI-Driven Conversation

### The Problem
The current wizard is rigid: 5 fixed steps, pick from a list, done. The user wants players to be able to say things like "I'm a big strong ox" and have the AI generate a character sheet that matches.

### The Flow

```
Player types /create or !create in #tavern-entrance (or whatever the designated channel is)
    ↓
Bot creates a PRIVATE THREAD named "⚔️ {username}'s Adventure"
    ↓
Bot posts opening message:

    "Welcome, traveler. I am Sir Thaddeus, keeper of fates.
     Tell me about yourself. Who are you? What are you?
     Speak freely — I'll shape your destiny from your words.

     (You can describe yourself however you like. Say things like
     'I'm a sneaky halfling who picks pockets' or 'I'm a massive
     orc barbarian who solves problems with his fists' — or just
     tell me your name and I'll ask questions.)"
    ↓
Player responds naturally: "I'm a big strong ox of a man, not too bright"
    ↓
AI generates a character concept:
  - Race: Human (or Half-Orc if it fits)
  - Class: Fighter or Barbarian
  - Stats: STR 15, CON 14, DEX 13, WIS 12, INT 8, CHA 10
  - Backstory seed from the description
    ↓
Bot presents the sheet as an embed:

    ┌─────────────────────────────────┐
    │ ⚔️ CHARACTER SHEET              │
    │                                 │
    │ Name: ???                       │
    │ Race: Human   Class: Barbarian  │
    │                                 │
    │ STR: 15 (+2)  DEX: 13 (+1)     │
    │ CON: 14 (+2)  INT:  8 (-1)     │
    │ WIS: 12 (+1)  CHA: 10 (+0)     │
    │                                 │
    │ HP: 22/22  MP: 4/4  Gold: 50   │
    │                                 │
    │ "A hulking brute who lets his   │
    │  fists do the talking..."       │
    └─────────────────────────────────┘

    "How does this look? You can say things like:
     • 'Make me stronger' / 'I want more charisma'
     • 'Change my class to Fighter'
     • 'My name is Grok'
     • 'I'm done, let's go' or 'looks good'"
    ↓
Player iterates: "make me even dumber but way stronger. name's Grok."
    ↓
AI adjusts: STR 17 (rerolled/manual), INT 6, Name = Grok
Bot re-presents the updated sheet
    ↓
Player: "perfect, let's go"
    ↓
Character created, thread becomes their game channel
Bot posts the starting room description
```

### Implementation Notes

- **AI does the heavy lifting.** Send the player's description to the narrator with a system prompt like: "You are creating a D&D character. Based on the player's description, suggest race, class, stat allocation, and a short backstory. Return JSON."
- **Stats still use the standard array** [15, 14, 13, 12, 10, 8] — the AI just decides the *assignment order* based on the description. "Big strong ox" → STR gets 15, INT gets 8.
- **Iterate until satisfied.** Keep the session alive until the player says a finalizing phrase. The AI adjusts stats/class/race on each turn.
- **Guardrails:** Stats must still come from the standard array (or rolled). The AI suggests allocation, not arbitrary numbers. Engine validates on creation.
- **Fallback:** If the AI is down, fall back to the current rigid wizard.

---

## 2. Per-Player Game Threads

### The Problem
Right now everything dumps into one channel. Multiple players typing commands creates chaos. You can't scroll back to see your story.

### The Solution: Private Threads

Each player gets their own Discord thread. This is their "game window."

```
#gae-tavern (main channel)
  ├── ⚔️ Grok's Adventure (thread — only Grok plays here)
  ├── ⚔️ Elara's Adventure (thread — only Elara plays here)
  └── ⚔️ Bongo's Adventure (thread)
```

### How It Works

- **Thread creation:** Bot creates a private thread when a player starts (`/create` or first command)
- **Thread ownership:** Bot only processes game commands from the thread owner inside their thread
- **Thread persistence:** Threads auto-archive after 24h of inactivity (Discord default). Bot can unarchive on the player's next command
- **Main channel:** Only used for `/create` and general chat. Game commands in the main channel get a reply: "Head to your adventure thread to play! (If you don't have one, type `/create`)"
- **Thread naming:** `⚔️ {CharacterName}'s Adventure` (renamed after character creation completes)

### Thread Content — The "Game Screen"

Since there's no UI panel, the thread IS the game. After every action, the bot posts a formatted response that gives the player everything they need:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📍 Korga's Forge
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Waves of heat pour from the open-air forge where a massive
orc woman hammers glowing steel into shape...

👤 NPCs: Korga Ironjaw
📦 Items: Thunderstrike Blade, Ironbark Plate Armor, Starfall Shield
🚪 Exits: south

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

After combat:
```
⚔️ COMBAT — Hollow Skeleton
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

🎲 Attack Roll: [17] + 2 = 19 vs AC 12 — HIT!
🎲 Damage: [6, 4, 7] + 5 = 22

The skeleton crumbles to dust under your thunderous blow.

💀 Hollow Skeleton defeated!
⭐ +20 XP  💰 +6 gold
🎁 Loot: Rusted Longsword

❤️ HP: 21/21  ✨ MP: 4/4  💰 Gold: 56
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### Room Description Strategy

Every time the player enters a room or types `look`, post the full formatted block:
- Room name (bold header)
- Narrated description (atmospheric text from the narrator)
- NPCs present (with quick disposition hint: "Mara the Barkeep (friendly)")
- Items on the ground
- Available exits
- Player status bar (HP/MP/Gold) as a footer

This replaces the UI's room panel. The thread scroll becomes the story log.

---

## 3. Identity & Session Management

### Discord ID = Player ID
- Already implemented. `PlayerCharacter.Id` = Discord user ID as string
- No login, no auth. If your Discord ID matches a saved player, you're in
- `GetPlayerByDiscordIdAsync()` already exists

### Restart / Reroll
Player types `!restart` or `/restart` in their thread:

```
Bot: "Are you sure you want to restart? Your character will be
      reset to level 1 at the tavern. All items and progress
      will be lost. NPCs will forget you existed.
      Type 'yes' to confirm."

Player: "yes"

Bot: [Resets player to spawn, clears inventory/equipment/gold,
      resets all NPC dispositions, clears story log for this player]

Bot: "Your fate has been unwound. Let's begin again.
      Tell me — who are you this time?"
      [Re-enters character creation flow]
```

### What "NPCs forget" means mechanically:
- Reset `NpcDispositionState` to defaults (emotion: "neutral", intensity: baseline)
- Clear all `MemoryFlags` for this player
- Clear story entries for this player
- Don't touch other players' state or world state

---

## 4. Command UX for Discord

### Slash Commands vs Prefix Commands

**Recommendation: Both.** Register Discord slash commands for discoverability, but also keep `!` prefix support for speed.

| Slash Command | Prefix | Description |
|--------------|--------|-------------|
| `/create` | `!create` | Start character creation |
| `/restart` | `!restart` | Reset character |
| `/stats` | `!stats` | View character sheet |
| `/inventory` | `!inv` | View inventory & equipment |
| `/help` | `!help` | Show commands |
| `/map` | `!map` | Show known rooms (see below) |

**Gameplay commands stay prefix-only** (more natural for RP):
- `!go north` / `!north` / `!n`
- `!look` / `!look at mara`
- `!attack skeleton`
- `!talk to mara` → then freeform (no prefix needed during conversation)
- `!take sword`
- `!equip sword`
- `!use potion`
- `!drop sword`
- `!flirt with mara` (freeform, triggers social check)

### Conversation Mode — No Prefix Needed

When in `InteractionMode.Conversation` or `InteractionMode.Combat`, the player shouldn't need `!` prefixes. Everything they type goes to the game:

```
Player: !talk to mara
Bot:    Mara looks up from polishing a mug. "Well hello there, stranger..."

Player: tell me about the dungeon        ← no prefix needed
Bot:    Mara leans in close. "The Dread Hollow? Nobody goes there anymore..."

Player: flirt with her                    ← still no prefix
Bot:    🎲 Charm (CHA +0): [14] vs DC 7 — SUCCESS
        Mara's cheeks flush. "Aren't you a bold one..."

Player: !leave                            ← prefix to exit conversation
Bot:    You step away from the bar. Mara watches you go with a smile.
```

**Exit keywords** (always work, with or without prefix): `leave`, `bye`, `walk away`, `stop talking`, `go <direction>`

---

## 5. The Map Problem

No visual map in Discord. Options:

### Option A: ASCII Map (Recommended)
When player types `!map`, generate a simple ASCII map of discovered rooms:

```
📍 YOUR MAP (discovered rooms)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

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
??? = Unexplored (you know it exists but haven't been there)
```

This can be generated from room exit data. Only show rooms the player has visited (`IsDiscovered`). Show connected-but-unvisited rooms as `???`.

### Option B: Just List Exits
Simpler — on every room entry, show exits clearly:
```
🚪 Exits: north (Blacksmith), south (General Store), east (Town Gate)
```

### Recommendation
Do both. Show exits on every room entry (Option B), and have `!map` generate the ASCII overview (Option A). The ASCII map is a nice "where have I been" feature.

---

## 6. Embeds vs Plain Text

### Current: Plain text with markdown
Works but looks messy for structured data like character sheets and combat results.

### Recommendation: Embeds for structured data, plain text for narration

**Use embeds for:**
- Character sheet (`!stats`)
- Inventory (`!inv`)
- Combat results (attack rolls, damage, loot)
- Room entry header (name, exits, NPCs, items)
- Map

**Use plain text for:**
- Narration / story text (the atmospheric stuff)
- Conversation dialogue
- Free-form action results

**Example — Room Entry:**
```
[EMBED: "Korga's Forge" — color: orange]
Description field: "Waves of heat pour from the open-air forge..."
Fields:
  NPCs: Korga Ironjaw
  Items: Thunderstrike Blade, Ironbark Plate, Starfall Shield
  Exits: ← south (Town Square)
Footer: ❤️ 21/21  ✨ 4/4  💰 50g

[Plain text below embed:]
Korga looks up from her anvil and grins. "Another adventurer
looking to get themselves killed. At least pick up something
sharp first."
```

---

## 7. Thread Lifecycle & Edge Cases

| Scenario | Behavior |
|----------|----------|
| Player creates character | Thread created, named after character |
| Player returns after thread archived | Bot unarchives thread on next command |
| Player tries to play in main channel | Bot replies: "Head to your thread!" with a link |
| Player tries to play in someone else's thread | Bot ignores (only thread owner's commands count) |
| Player has no character and types a game command | Bot replies: "You don't have a character yet. Type `/create` to begin." |
| Player types `/create` but already has a character | Bot replies: "You already have a character! Type `/restart` to start over." |
| Bot restarts / reconnects | Threads persist in Discord. Bot re-discovers them via player ID lookup |
| Thread gets manually deleted | Player can type `/create` again, bot makes a new thread |

---

## 8. Implementation Priority

### Phase 1 — Minimum Viable Discord Game
1. Private thread per player (create on `/create`)
2. AI-driven character creation conversation
3. Prefix-free conversation/combat mode
4. Formatted room entry blocks (embed + narration)
5. `!stats`, `!inv`, `!help` with embeds
6. `!restart` with NPC disposition reset

### Phase 2 — Polish
7. Slash command registration (discoverability)
8. ASCII map generation (`!map`)
9. Status bar footer on every response
10. Thread auto-unarchive on return

### Phase 3 — Nice-to-Have
11. Spectator mode (other users can read the thread but not issue commands)
12. Party system (multiple players in same thread/room)
13. DM override channel (admin can intervene in any thread)
14. Achievement announcements in main channel ("🏆 Grok has defeated Goretusk!")

---

## Technical Notes

### Thread API
Discord.Net supports thread creation:
```csharp
var thread = await textChannel.CreateThreadAsync(
    name: $"⚔️ {playerName}'s Adventure",
    type: ThreadType.PrivateThread,
    autoArchiveDuration: ThreadArchiveDuration.OneDay
);
await thread.SendMessageAsync("Welcome, traveler...");
```

### Slash Command Registration
Discord.Net supports slash commands via `SlashCommandBuilder`:
```csharp
var command = new SlashCommandBuilder()
    .WithName("create")
    .WithDescription("Create a new character and begin your adventure");
await client.CreateGlobalApplicationCommandAsync(command.Build());
```

### AI Character Creation Prompt Shape
```
System: You are a character creation assistant for a D&D-style text adventure.
The player will describe their character in natural language. Based on their
description, generate a character sheet.

You MUST use the standard array [15, 14, 13, 12, 10, 8] for stats.
Assign them based on the player's description (strong character → STR gets 15, etc.)

Return JSON:
{
  "name": "suggested name or null if player didn't specify",
  "race": "Human|Elf|Dwarf|Halfling|Orc|Tiefling",
  "class": "Fighter|Mage|Rogue|Cleric|Ranger|Bard",
  "stats": { "str": 15, "dex": 14, ... },
  "backstory": "2-3 sentence backstory based on their description",
  "followUpQuestion": "optional question if description was vague"
}
```

### NPC Disposition Reset on Restart
```csharp
// For each room, reset NPCs that have memory of this player
foreach (var room in allRooms)
    foreach (var npc in room.Npcs)
    {
        npc.DispositionState = new NpcDispositionState();
        // Memory flags are per-NPC, not per-player, so this is a
        // simplification. If we want per-player disposition, that's
        // a model change (Dictionary<playerId, NpcDispositionState>).
    }
```

**Important caveat:** NPC disposition is currently global, not per-player. If Grok insults Mara, she's mad at everyone. For a multi-player game, we'd need `Dictionary<string, NpcDispositionState>` keyed by player ID. Worth flagging as a future model change.
