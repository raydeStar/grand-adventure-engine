# P03 — Dashboard UI Baseline

**Category:** Polish
**Effort:** Medium-Large
**Touches:** `src/GAE.Dashboard.Api/wwwroot/`, dashboard API endpoints

## What

The dashboard exists as mostly static HTML with API endpoints behind it. For MVP, it doesn't need to be fancy, but it should at least let an admin see what's going on without hitting raw API endpoints.

## Minimum Viable Dashboard

- [ ] **Player list** — See all active players, their level, HP, current room
- [ ] **Player detail** — Click a player to see full stats, inventory, quest log
- [ ] **Game health** — Is the narrator responding? How many players active? Any errors?
- [ ] **Basic styling** — Doesn't need to be beautiful, but shouldn't look like unstyled HTML from 1997

## Nice-to-Have (skip for MVP if needed)

- [ ] NPC state viewer
- [ ] Room viewer / map
- [ ] Manual mutation tools (grant items, teleport, heal)
- [ ] Real-time updates via SignalR (skeleton exists but not wired)

## Acceptance Criteria

- [ ] Dashboard loads at `localhost:8181` and shows player list without errors
- [ ] Admin login required (existing auth system)
- [ ] Data comes from actual API endpoints, not hardcoded
- [ ] Works in Chrome and Firefox at minimum

## Notes

- SignalR hub skeleton exists — wiring it for live updates would be great but isn't blocking.
- Admin console endpoints already exist (grant items, teleport, etc.) — the UI just needs to call them.
- Playwright E2E tests already cover some dashboard flows. Extend those as you build out the UI.
