# Grand Adventure Engine: Co-DM

## Submission links

- Live demo: <https://projectbonk-vs-demo.calmpond-0e183dc4.centralus.azurecontainerapps.io/>
- Public repository: <https://github.com/raydeStar/grand-adventure-engine>
- Demo video: **Add the public YouTube URL before submitting.**

## Tagline

**Give your AI Dungeon Master memory, rules, and a human co-pilot.**

## Problem

Long-running AI roleplay often collapses because the model is expected to invent, remember, and enforce the whole world from conversational context. NPCs learn secrets they should not know, quests drift, consequences disappear, and human DMs must manually reconcile state.

## Solution

Grand Adventure Engine already separates narration from authoritative mechanics. WebMCP now gives an AI Co-DM semantic access to the same signed-in DM Console used by the human. It can inspect the current scene, search real lore and quests, communicate with one player, and stage mechanical consequences for human approval.

The agent does not become the rules engine. It sees evidence, responds in-world, and proposes. The human decides. Existing validation and PostgreSQL persistence make an approved outcome stick.

## Why WebMCP

Traditional browser automation would need to scrape player cards, room views, search results, story logs, and entity editors through fragile visual interactions.

WebMCP exposes those operations as structured site tools tied to the current page and authenticated session. The human and agent see the same selected player, evidence, activity, proposed actions, and resulting state changes.

## What people and agents do together

- A player creates an unexpected roleplay situation.
- The agent inspects authoritative player, room, NPC, quest, lore, and recent-story state.
- The agent searches the living world instead of inventing an answer.
- The agent sends a bounded in-world response to exactly one player.
- The agent proposes the smallest persistent consequence.
- The human DM reviews its evidence and exact payload, then approves or rejects it.
- Existing game rules and persistence make an approved outcome visible and durable.

## Implementation

- imperative `document.modelContext.registerTool` registration in the top-level dashboard page;
- five narrow tools with bounded JSON Schemas and `additionalProperties: false`;
- the existing cookie-authenticated API client and Admin authorization policy;
- one shared `window.GaeCoDm` service used by both WebMCP handlers and visible controls;
- existing DM search, entity detail rendering, SignalR updates, and game mutation endpoints;
- an app-owned durable proposal queue that cannot mutate game state without a one-time human approval;
- idempotent Player Flow messages plus review-gated Discord delivery;
- graceful normal-dashboard behavior when WebMCP is absent.

## Safety and human control

The site tools expose no reset, delete, arbitrary JSON, arbitrary endpoint, model-switching, world-deletion, or player-deletion capability. Teleports can target only an existing room and require confirmation. Large resource changes require confirmation. A rejected proposal calls no game API. Credentials, cookies, narrator prompts, keys, and connection strings never enter tool output.

## Pre-existing work versus new work

Pre-existing: the RPG engine, Player Flow, Admin Console, DM search and entity browser, PostgreSQL persistence, Moonfall campaign, narrator integrations and fallbacks, SignalR, Discord support, existing admin mutations, Docker deployment, and tests.

New for the challenge: WebMCP site tools, selected-player Co-DM context, agent activity receipts, intervention proposal cards, human approval/rejection orchestration, player-visible DM message persistence independent of Discord, focused WebMCP verification, and demo/deployment/submission documentation.

## Demo proof

The two-surface demo shows Ari Quickstep make an unsupported claim to Mara Vale. The browser agent inspects Ari's genuine quest log and current room, searches Mara and The Waterway Infestation, sends a grounded response, and proposes `Under Suspicion`. The human approves the proposal; the Player Flow shows the message and refreshed status. No Discord token is required.

## Project gallery captions

1. **The AI Co-DM shares the human DM's live campaign context.** Ari's selected scene and the browser agent's inspection activity are visible together in the deployed Admin Console.
2. **The agent proposes; the human decides.** The approved `Under Suspicion` intervention shows its rationale, evidence, exact mechanical effect, and all five registered WebMCP tools.
3. **The result reaches the player, not a debug console.** Ari's unsupported quest claim and Mara's grounded Co-DM response appear in the full-width Player Flow.
