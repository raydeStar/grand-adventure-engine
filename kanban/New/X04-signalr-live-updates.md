# X04 — Dashboard Real-Time Updates via SignalR

**Category:** Post-MVP
**Effort:** Medium-Large
**Touches:** `GAE.Dashboard.Api/`, SignalR hubs, dashboard UI

## What

SignalR hub skeleton exists but isn't wired to anything. The dashboard should update in real time — when a player moves, fights, or levels up, the admin sees it without refreshing.

## Acceptance Criteria

- [ ] SignalR hub broadcasts game events (player move, combat, level up, death, quest complete)
- [ ] Dashboard UI subscribes and updates in real time
- [ ] Connection status indicator (connected/reconnecting/disconnected)
- [ ] Event log panel showing recent game events as they happen
