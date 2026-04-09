<!-- TODO: Create a banner image (~1200x400) showing a fantasy tavern scene
     with a Discord chat overlay on one side and dice/character sheet on the other.
     Style: pixel art or stylized fantasy illustration. -->
<!-- ![Grand Adventure Engine Banner](docs/images/banner.png) -->

# Grand Adventure Engine

**A multiplayer text RPG that lives in your Discord server — powered by AI storytelling and real game mechanics.**

You and your friends play a fantasy adventure right inside Discord. An AI narrator describes the world around you, reacts to your choices, and brings characters to life — but the actual game rules (combat, loot, skill checks) are handled by a fair, consistent engine under the hood. The AI tells the story. The math keeps it honest.

<!-- TODO: Create a screenshot (~800x500) of a Discord thread showing a player
     exploring a room, with the bot narrating the scene and presenting choices.
     Capture a real session or mock one up in Discord. -->
<!-- ![Gameplay Screenshot](docs/images/gameplay-discord.png) -->

---

## What Can You Do?

**Create a Character by Just Talking** — No spreadsheets. No stat calculators. Just tell the game who you want to be — *"I want to be a sneaky goblin alchemist"* — and it builds your full character from there. Race, class, stats, backstory, starting gear, all of it.

**Explore and Fight** — Wander through rooms, talk to characters, pick up items, and get into turn-based battles with HP, magic, and gold on the line. Die? You wake up at the tavern, a little poorer but ready to try again.

**Your World, Your Story** — Every player gets their own version of the world. If you loot a chest or defeat a monster, that's *your* experience — it doesn't change things for other players.

**Take On Quests** — Hunt monsters, deliver packages, discover hidden places. The game tracks your progress and rewards you when you're done.

**Meet Characters Who Feel Alive** — NPCs remember how you've treated them. Some are friendly. Some are not. How they act depends on the game rules — how they *talk* depends on the AI.

**New here?** Read the [Player Guide](docs/player-guide.md) for a full walkthrough of commands, combat, and quests.

<!-- TODO: Create a screenshot (~800x500) of the browser dashboard showing
     the admin view with player state, NPC panels, or quest tracking visible.
     Use the running dashboard at localhost:8181 with the admin account. -->
<!-- ![Dashboard Screenshot](docs/images/dashboard.png) -->

---

## How It Works

> **The AI is the storyteller, not the referee.**

The AI's job is to make the game feel alive — describing scenes, voicing characters, reacting to the unexpected. But it never decides whether your attack hits or how much gold you find. That's all handled by the game engine, the same way every time, for every player. Fair and square.

There's an admin dashboard for managing players, editing lore, and inspecting the game in real time.

*"The architecture is sound. The rules are fair. The dice are ready."* — Sir Thaddeus

---

## Want to Run Your Own?

This is a self-hosted project — you run it on your own machine or server. Here's what you'll need:

| What | Why |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/) | Runs the game engine |
| [Node.js 20+](https://nodejs.org/) | Powers the dashboard and tests |
| [LM Studio](https://lmstudio.ai/) | Runs the AI locally on your machine (free, private, no API keys) |
| [Docker](https://www.docker.com/) | Runs supporting services |
| A Discord bot token | Connects the game to your Discord server ([how to get one](https://discord.com/developers/applications)) |

### Setup

**Full step-by-step instructions:** [Self-Hosting Setup Guide](docs/setup-guide.md)

Quick start:

```bash
# Build the project
dotnet build GrandAdventureEngine.slnx

# Copy and edit the example environment config
cp .env.example .env
# Edit .env with your Discord token and any password changes

# Start the dashboard
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1
```

Once it's running, open your browser to `http://localhost:8181` for the dashboard. If that port is already in use, the script picks a new one and tells you.

### Default Logins

| Role | Username | Password |
|---|---|---|
| Player | `user` | `GAE-User-Local!123` |
| Game Master | `admin` | `GAE-Admin-Local!123` |

---

## For Developers

<details>
<summary>Architecture & technical details</summary>

### Project Structure

| Module | What It Does |
|---|---|
| **GAE.Core** | Game data models and shared contracts (no external dependencies) |
| **GAE.Engine** | The game rules — combat, loot, skill checks, state management |
| **GAE.Narrator** | Connects to your local AI (LM Studio) for narration |
| **GAE.WikiSync** | *(deprecated — world data now loaded from YAML seed files via content registry)* |
| **GAE.Discord** | The Discord bot that players interact with |
| **GAE.Dashboard.Api** | Web dashboard with real-time updates |

### Running Tests

```bash
# Unit & integration tests
dotnet test

# Browser-based end-to-end tests
npm run test:e2e
```

### More Docs

- [Player Guide](docs/player-guide.md) — How to play: commands, combat, quests, tips
- [Self-Hosting Setup Guide](docs/setup-guide.md) — Full installation and configuration walkthrough
- [Operator Guide](docs/dashboard-ops.md) — Dashboard admin workflows, environment variables, manual operations

</details>

---

## License

<!-- TODO: Choose and add a license (MIT, GPL, etc.) -->

*License not yet specified.*
