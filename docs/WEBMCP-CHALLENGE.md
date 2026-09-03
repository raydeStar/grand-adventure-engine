# WebMCP Co-DM Challenge

## 1. Prior product state

Before this challenge run, Grand Adventure Engine already contained the rules-first RPG engine, PostgreSQL persistence, browser Player Flow, optional Discord integration, authenticated Admin Console, unified DM search and entity browser, narrator integrations and grounded fallbacks, administrator mutation endpoints, SignalR updates, tests, Docker deployment assets, Moonfall content, and Apache 2.0 licensing.

## 2. Challenge-period additions

The challenge work adds only the Co-DM layer:

- a compact selected-player context panel inside the existing DM Console;
- five top-level imperative WebMCP site tools;
- a bounded browser-agent activity receipt log;
- a visible intervention proposal queue;
- human approval or rejection through existing mutation APIs;
- player-visible, persisted DM messages with optional Discord mirroring;
- focused WebMCP and message-delivery tests;
- challenge demo, deployment, submission, and readiness documents.

The challenge work does not claim the underlying engine, world content, persistence, narrator, Discord bot, dashboard, DM search, or existing administrator mutations as new work.

## 3. Baseline commit SHA

Baseline: `01a709fa54d606ad666e2643c29e12c8f4a11de3`

The working tree was clean at the start of the run on 2026-09-03.

## 4. Changed files

| File | Challenge change |
|---|---|
| `src/GAE.Dashboard.Api/wwwroot/js/co-dm.js` | Shared Co-DM application service, bounded context, receipts, proposals, approval flow, and rendering |
| `src/GAE.Dashboard.Api/wwwroot/js/webmcp.js` | Feature-detected registration of five imperative site tools |
| `src/GAE.Dashboard.Api/wwwroot/index.html` | Co-DM panel and cache-busted script/style references |
| `src/GAE.Dashboard.Api/wwwroot/css/style.css` | Compact responsive Co-DM styling within the terminal aesthetic |
| `src/GAE.Dashboard.Api/wwwroot/js/api.js` | World-aware story requests and selected-player room-instance reads |
| `src/GAE.Dashboard.Api/wwwroot/js/app.js` | Authentication, player-list, SignalR, and refresh integration |
| `src/GAE.Dashboard.Api/Controllers/DashboardController.cs` | Authoritative player-room reads and persisted player-visible DM messages |
| `browser-tests/dashboard.spec.js` | WebMCP registration, shared-service, proposal, approval, rejection, message, and unsupported-browser coverage |
| `tests/GAE.Integration.Tests/AdminConsoleTests.cs` | No-Discord persisted DM-message coverage |
| `README.md` and challenge documents | Honest product boundary, demo, deployment, and submission guidance |

## 5. WebMCP architecture

`webmcp.js` runs in the top-level dashboard page and feature-detects `document.modelContext.registerTool`. It registers at most five tools and prevents duplicate registration. Unsupported browsers simply report `WebMCP supported: no`; no ordinary dashboard path depends on WebMCP.

All tool handlers call `window.GaeCoDm`, the same service used by the visible player selector, refresh button, message form, and proposal cards:

1. `get_dm_context` refreshes a genuine player record, that player's current room instance, bounded active status effects, world-filtered recent story, interaction state, and available health telemetry.
2. `search_world` calls the existing DM search endpoint and renders the same result cards used by the human console.
3. `inspect_entity` uses existing player, room, registry, and room/NPC data, then opens the existing detail panel.
4. `send_dm_message` targets one non-empty player ID and calls the existing `API.sendMessage` path.
5. `propose_dm_intervention` writes only app-owned proposal metadata; it never calls a game mutation API.

Arrays, strings, tool outputs, activity receipts, and proposal history are bounded. The proposal queue and receipts use local storage; authoritative game state remains server-owned.

## 6. Human-agent workflow

The human chooses one player. The agent inspects the same visible context, searches and opens evidence in the existing DM browser, sends one grounded player-visible message, and creates the smallest suitable proposal. The proposal card exposes its rationale, evidence IDs, and exact payload. A human click is required to approve or reject it.

Approval revalidates the proposal and calls exactly one existing API: grant item, apply status, adjust resources, or teleport. Rejection only updates local proposal metadata. A successful approval refreshes the selected player's context, while existing SignalR admin events trigger a debounced refresh for relevant changes.

## 7. Safety model

- Existing cookie authentication and the Admin authorization policy remain authoritative.
- The message tool requires one explicit player ID, rejects blank input, and caps messages at 800 characters.
- The proposal tool exposes no deletion, reset, arbitrary JSON, arbitrary endpoint, model, world-management, or player-management operation.
- Grant-item approval resolves an existing registry item before calling the existing mutation.
- Teleport approval sets `createRoomIfMissing: false` and `connectFromCurrentRoom: false`.
- Teleports and resource deltas larger than 100 require an additional browser confirmation.
- Rejected proposals call no mutation API.
- Discord delivery is optional. The in-game story message succeeds independently; a failed mirror is logged and reported without erasing the in-game delivery.
- WebMCP never receives credentials, cookies, prompts, API keys, connection strings, or private configuration.

## 8. Current limitations

- Detailed combat turn state is not exposed by the existing dashboard API; the context reports interaction mode and names this limitation.
- The dashboard exposes no reliable Discord connection telemetry, so the context returns `null` rather than inventing a status.
- Proposal and activity history are browser-local metadata, not multi-admin shared records.
- Proposal editing is not implemented; a human can reject and request a corrected proposal.
- The optional `check_knowledge_boundary` stretch tool is intentionally omitted. Existing world search exposes lore and NPC scopes, but it does not prove a complete per-NPC exclusion set without inventing logic.
- WebMCP availability depends on the judging browser. The stubbed Playwright test proves the registration and execution contract when the imperative API is present.

## 9. Demo instructions

Use [WEBMCP-DEMO-SCENARIO.md](WEBMCP-DEMO-SCENARIO.md). The deterministic path uses seeded player `demo-user` (Ari Quickstep), current room `spawn` (The Lantern's Rest), NPC `innkeeper_mara` (Mara Vale), quest `waterway_infestation`, and lore entry `lore-mara`. Discord is not required.

## 10. Deployment notes

Use [DEPLOYMENT_WEBMCP.md](../DEPLOYMENT_WEBMCP.md). The existing Dockerfile and Compose topology remain the deployment source of truth. No authentication bypass, public deployment, or remote MCP server was added during this run.
