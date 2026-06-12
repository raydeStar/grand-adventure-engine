# Grand Adventure Engine

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

Grand Adventure Engine is a self-hosted text RPG engine for Discord and the web. Players type natural-language actions; the engine resolves the mechanics; an AI narrator turns the results into short, flavorful prose.

It is not a chatbot wearing a cape. It is a game simulation with persistent state, combat rules, quests, NPC disposition, world lore, and a narrator that reacts to what actually happened.

## What It Does

- **Natural-language play:** players can type commands like `look`, `attack`, and `take sword`, or try messier actions like "search the rafters for a hidden latch."
- **AI-assisted character creation:** describe a character concept and the engine builds a playable sheet with stats, gear, traits, and backstory.
- **Rules-first outcomes:** combat, skill checks, inventory, loot, currency, and quest progress are resolved by the engine instead of improvised by the model.
- **Persistent NPC reactions:** NPCs track disposition and remember how players treated them during the session.
- **Scoped world knowledge:** NPCs only answer from lore they should plausibly know.
- **Discord plus dashboard:** play in Discord, or use the web dashboard to inspect state, manage content, and run the game locally.
- **Multi-world support:** worlds can carry their own rules, content, portals, NPC state, and stat translation behavior.

New players should start with the [Player Guide](docs/player-guide.md).

## Screens

The project currently ships with dashboard visual-test snapshots under `browser-tests/`. Public-facing screenshots are still pending, so this README avoids fake hero art and stale mockups.

## How It Works

The AI narrator is responsible for presentation: room descriptions, NPC voice, and consequences written in the tone of the game. The engine is responsible for decisions: whether an attack hits, whether a quest advances, what state changes, and what the player can do next.

At a high level:

```text
Player input
  -> command parser
  -> game engine mechanics
  -> narrator prompt with bounded context
  -> persisted state plus narration
```

If a command is not recognized, it routes through the free-form action path instead of returning a canned fallback. The goal is that every player input matters.

## Project Structure

| Path | Purpose |
| --- | --- |
| `src/GAE.Core` | Models and shared interfaces |
| `src/GAE.Engine` | Game rules, command parsing, quests, combat, persistence |
| `src/GAE.Narrator` | LM Studio, Ollama, and OpenAI-compatible narrator integration |
| `src/GAE.Dashboard.Api` | ASP.NET Core API, SignalR hub, and static dashboard |
| `src/GAE.Discord` | Discord bot service |
| `config` | Rules, lore, quests, monsters, classes, races, and item seeds |
| `tests` | Unit, integration, and narrator tests |
| `browser-tests` | Playwright end-to-end and visual tests |
| `docs` | Player, setup, ops, and design notes |

## Requirements

| Tool | Why |
| --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Builds and runs the app |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Runs the app and PostgreSQL stack |
| [LM Studio](https://lmstudio.ai/) or [Ollama](https://ollama.com/) | Local AI narrator backend |
| [Node.js 20+](https://nodejs.org/) | Browser tests |
| Discord bot token | Optional, only needed for Discord play |

## Quick Start

For full setup instructions, use the [Self-Hosting Setup Guide](docs/setup-guide.md).

```powershell
dotnet build GrandAdventureEngine.slnx
Copy-Item .env.example .env
notepad .env
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1
```

Open the dashboard at `http://localhost:8181`, or use the URL printed by the startup script if it picked another port.

Default local logins:

| Role | Username | Password |
| --- | --- | --- |
| Player | `user` | `GAE-User-Local!123` |
| Admin | `admin` | `GAE-Admin-Local!123` |

Change the default dashboard and database passwords before exposing the app outside your local machine.

## Running Tests

```powershell
dotnet test
npm run test:e2e:visual:safe
```

Useful targeted commands:

```powershell
dotnet test tests/GAE.Engine.Tests
dotnet test tests/GAE.Integration.Tests
npm run test:e2e:docker
```

Browser tests expect a running app. Prefer the `:safe` Playwright scripts when updating visual snapshots.

## Documentation

- [Self-Hosting Setup Guide](docs/setup-guide.md)
- [Player Guide](docs/player-guide.md)
- [Dashboard Operator Guide](docs/dashboard-ops.md)
- [Known Gaps](docs/known-gaps.md)
- [Multi-World Scope](docs/MULTI-WORLD-SCOPE.md)
- [Database Migration Scope](docs/DATABASE-MIGRATION-SCOPE.md)

## Content Note

The bundled YAML seeds are original demo content intended for local development and public examples. Audit and replace them before running a long-lived public server with your own setting.

## Security Notes

- Do not commit real `.env` files, Discord tokens, production connection strings, cookies, or database dumps.
- Rotate any token that was ever committed, even if it has since been removed.
- Treat the default passwords as local-development placeholders only.
- Production hides dashboard password hints by default. Keep `GAE_DASHBOARD_SHOW_LOGIN_PASSWORDS=false` unless you are running a private local demo.
- The dashboard includes admin functionality; put it behind proper network controls before internet-facing deployment.

## License

Licensed under the Apache License 2.0. See [LICENSE](LICENSE) for details.
