# Grand Adventure Engine

A Discord-based, AI-narrated, multiplayer choose-your-own-adventure RPG engine.

## Architecture

- **GAE.Core** — Domain models and interfaces (zero external dependencies)
- **GAE.Engine** — Deterministic game engine (rules, probability, state)
- **GAE.Narrator** — LLM integration via LM Studio
- **GAE.WikiSync** — Wiki.js GraphQL client for persistent world state
- **GAE.Discord** — Discord.NET bot
- **GAE.Dashboard.Api** — ASP.NET Core Web API + SignalR hub + embedded browser dashboard

## Prerequisites

- .NET 10 SDK
- Node.js 20+
- LM Studio (local LLM)
- Wiki.js (via Docker or standalone)
- Discord Bot token

## Quick Start

```bash
# Build
dotnet build GrandAdventureEngine.slnx

# Run tests
dotnet test

# Start the local stack
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1

# Browser E2E
npm run test:e2e
```

`reset-docker-stack.ps1` publishes the dashboard on `http://localhost:8181` and Wiki.js on `http://localhost:3000` by default, but will automatically move either service to the next free host port if those ports are already busy.

## Browser Dashboard

- Local URL: `http://localhost:8181` by default. Check the `reset-docker-stack.ps1` output for the actual URL if that port is already occupied.
- Embedded in `src/GAE.Dashboard.Api/wwwroot`
- Supports protected user and admin flows, live state inspection, manual mutation tools, and Playwright visual regression coverage

## Operator Guide

- Manual browser and PowerShell workflows: [docs/dashboard-ops.md](docs/dashboard-ops.md)
- Default local credentials:
	- `user` / `GAE-User-Local!123`
	- `admin` / `GAE-Admin-Local!123`

## Philosophy

> The LLM is the storyteller, not the referee. All mechanical outcomes are computed by deterministic C# code.
> The LLM narrates what the engine already decided. The wiki is the source of truth for persistent world state.

*"The architecture is sound. The rules are fair. The dice are ready."* — Sir Thaddeus
