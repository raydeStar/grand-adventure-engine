# Known Gaps & Production Boundary

Grand Adventure Engine is production-ready for a **single self-hosted instance used by a solo player or trusted group**. It is not yet a hostile public multi-tenant platform. This page records that boundary plainly so operators can make an informed choice instead of discovering it during a goblin-related incident.

## Before exposing it to the public internet

### Character ownership is shared

The regular dashboard account can resume any character when given its player ID. The engine does not yet bind characters to separate human accounts. Treat every signed-in regular user as a trusted member of the same table.

**Needed for public multi-tenancy:** individual user accounts, player-to-owner records, ownership checks on player APIs and SignalR subscriptions, recovery flows, and an audit trail for ownership changes.

### One application instance is the supported topology

PostgreSQL persists game state, but SignalR delivery and several runtime coordinators are process-local. Running multiple application replicas without a backplane and distributed coordination can produce incomplete live updates or competing background work.

**Needed for high availability:** a SignalR backplane, distributed locks for seed/bootstrap work, cross-instance cache invalidation, and load tests against the chosen deployment topology.

### TLS terminates outside the application

The provided Docker stack serves HTTP. Put it behind a trusted HTTPS reverse proxy before allowing remote access. Keep the database port firewalled from untrusted networks even when its password is strong.

### Public Discord servers need moderation policy

The engine simulates free-form player input and sends it to the configured narrator. It does not provide a complete public-community moderation, abuse reporting, or per-user quota system. Discord permissions, narrator safety settings, retention policy, and acceptable-use rules remain operator responsibilities.

## Operational realities

- Narration quality and latency depend heavily on the selected model, context size, and hardware. The deterministic game continues with contextual local fallbacks when the narrator is unavailable, but prose quality will differ.
- Back up both the PostgreSQL volume and `/app/data` before upgrades. Restore tests are an operator responsibility; an untested backup is merely an optimistic file.
- The bundled campaign is authored and playable, but AI narration makes the exact route and duration variable. Test your chosen model before inviting a crowd.
- Codex CLI narration is intentionally opt-in and may incur cloud usage, cost, and substantially higher latency than a local backend.

## Closed gaps

The earlier demo audit identified mechanical item use, multi-enemy combat, trading, and auto-equip. All four are implemented and covered by the current engine tests. They remain here only as a modest reminder that old gap lists should not become archaeology exhibits.
