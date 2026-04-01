# Grand Adventure Engine

A Discord-based, AI-narrated, multiplayer choose-your-own-adventure RPG engine.

## Architecture

- **GAE.Core** — Domain models and interfaces (zero external dependencies)
- **GAE.Engine** — Deterministic game engine (rules, probability, state)
- **GAE.Narrator** — LLM integration via LM Studio
- **GAE.WikiSync** — Wiki.js GraphQL client for persistent world state
- **GAE.Discord** — Discord.NET bot
- **GAE.Dashboard.Api** — ASP.NET Core Web API + SignalR hub
- **GAE.Dashboard.Client** — React + Vite frontend (coming soon)

## Prerequisites

- .NET 9 SDK
- Node.js 20+
- LM Studio (local LLM)
- Wiki.js (via Docker or standalone)
- Discord Bot token

## Quick Start

```bash
# Build
dotnet build GrandAdventureEngine.sln

# Run tests
dotnet test

# Start Wiki.js (Docker)
docker compose up -d wikijs

# Run the API + Discord bot
cd src/GAE.Dashboard.Api
dotnet run

# Dashboard (when available)
cd src/GAE.Dashboard.Client
npm install && npm run dev
```

## Philosophy

> The LLM is the storyteller, not the referee. All mechanical outcomes are computed by deterministic C# code.
> The LLM narrates what the engine already decided. The wiki is the source of truth for persistent world state.

*"The architecture is sound. The rules are fair. The dice are ready."* — Sir Thaddeus
