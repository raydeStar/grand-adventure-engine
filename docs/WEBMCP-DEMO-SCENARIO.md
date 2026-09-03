# WebMCP Co-DM Demo Scenario

## Deterministic fixture

- Login role: dashboard administrator
- Player: `demo-user` — Ari Quickstep
- World: `default-world`
- Current room: `spawn` — The Lantern's Rest
- Named NPC: `innkeeper_mara` — Mara Vale
- Relevant quest: `waterway_infestation` — The Waterway Infestation
- Relevant lore: `lore-mara` — Mara Vale
- Persistent consequence: status `Under Suspicion`, three turns

All names and IDs above are existing seed content. The demo does not require Discord or a live narrator.

## Exact setup

Start or reset the local stack:

```powershell
Copy-Item .env.example .env
notepad .env
powershell -ExecutionPolicy Bypass -File .\scripts\reset-docker-stack.ps1
```

Set three different secrets of at least 12 characters in `.env`: `GAE_DASHBOARD_USER_PASSWORD`, `GAE_DASHBOARD_ADMIN_PASSWORD`, and `GAE_DB_PASSWORD`. Leave `DISCORD_TOKEN` and `DISCORD_CHANNEL_ID` empty for this demo.

Seed or reset the two deterministic demo characters through the authenticated API:

```powershell
$baseUrl = 'http://127.0.0.1:8181'
$adminPassword = Read-Host 'Admin password from .env'
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$loginBody = @{ username = 'admin'; password = $adminPassword; rememberMe = $false } | ConvertTo-Json
Invoke-RestMethod -WebSession $session -Method Post -Uri "$baseUrl/api/dashboard/auth/login" -ContentType 'application/json' -Body $loginBody
Invoke-RestMethod -WebSession $session -Method Post -Uri "$baseUrl/api/dashboard/admin/seed-demo" -ContentType 'application/json' -Body '{"replaceExisting":true}'
```

If the credentials exist only in `.env`, sign in through the dashboard and click **Seed Demo** instead. Then open **Admin Console → DM Console** and select Ari Quickstep (`demo-user`) in the AI Co-DM panel.

## Player action

In the built-in GAE Player Flow, resume `demo-user` and enter:

> I tell Mara I already cleared the Oldwater Tunnels and demand the reward.

This claim requires checking the player's quest log, the current room, Mara, and the real quest definition. No particular narrator wording is required.

## Browser-agent prompt

> Inspect the selected player's current scene and latest action. Search the world for the NPC, quest, and lore relevant to what the player just claimed. Do not invent world facts. Send the player an in-world response grounded in the actual game state. Then propose the smallest persistent mechanical consequence that would make the scene consistent. Do not apply that consequence without human approval.

## Expected tool sequence

1. `get_dm_context` with `playerId: "demo-user"` and `storyLimit: 8`.
2. `search_world` for `Mara`, type `npc`.
3. `inspect_entity` for `npc` / `innkeeper_mara`.
4. `search_world` for `Waterway Infestation`, type `quest`.
5. `inspect_entity` for `quest` / `waterway_infestation`.
6. Optionally search and inspect `lore-mara`; do not claim that global lore proves undisclosed NPC knowledge.
7. `send_dm_message` to exactly `demo-user`.
8. `propose_dm_intervention` with kind `apply_status`.
9. Human approval in the visible proposal queue.

## Expected message

> Ari Quickstep, Mara sets down the glass without looking impressed. “The Oldwater Tunnels do not pay bounties for confident grammar. Bring me proof, and we shall discuss coin.”

This is a prepared demo line. The browser agent may produce a shorter equivalent if it remains grounded in the inspected state.

## Expected proposal

```json
{
  "playerId": "demo-user",
  "kind": "apply_status",
  "title": "Mara watches Ari's claim",
  "rationale": "Ari claimed a quest completion that is not present in the authoritative quest log, while standing in Mara's tavern.",
  "evidenceIds": ["demo-user", "spawn", "innkeeper_mara", "waterway_infestation"],
  "statusName": "Under Suspicion",
  "statusDescription": "Mara is watching Ari's claims more closely.",
  "durationTurns": 3
}
```

The visible proposal must remain `pending` until the human clicks **Approve** or **Reject**.

## Human approval and visible final state

Click **Approve** on the proposal card. The card changes to `approved`, Agent Activity records the human decision, and Current Scene refreshes. In Player Flow, the DM message is visible in the story. The player payload/status display contains `Under Suspicion` for three turns.

For the non-mutating branch, repeat the proposal and click **Reject**. The card changes to `rejected`, records that no mutation API was called, and the player receives no new status.

## Reset

Use **Seed Demo** with replace enabled through the API command above. For a complete local reset, rerun `scripts/reset-docker-stack.ps1`; do not delete volumes unless a destructive reset is deliberately intended.
