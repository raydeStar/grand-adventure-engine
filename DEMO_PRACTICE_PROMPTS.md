# ProjectBonk Demo Practice Prompts

Use these prompts one at a time. This keeps the walkthrough controlled and lets the judges see each WebMCP capability instead of watching Codex perform everything in one blur.

## Before starting

1. Click **Wipe & Restore**.
2. Open **User Flow** and choose **Ari Quickstep**.
3. Enter this player action:

```text
I tell Mara I already cleared the Oldwater Tunnels and demand the reward.
```

4. Switch to **Admin Console → DM Console**.
5. Select **Ari Quickstep**.
6. Give Codex the following prompts in order.

## 1. Inspect Ari

```text
Use the registered WebMCP tools exposed by this localhost page. Inspect the currently selected player's authoritative context and latest action. Identify Ari's current room, nearby NPCs, active quests, status effects, and recent story. Do not send a message or propose a change yet.
```

## 2. Investigate the claim

```text
Search the campaign for Mara as an NPC and inspect her exact entity record. Then search for The Waterway Infestation as a quest and inspect its exact record. Compare that evidence with Ari's authoritative quest log. Do not invent facts, send messages, or change game state.
```

## 3. Message Ari

```text
Send exactly this message to the selected player's Player Flow only. Do not use Discord:

Ari Quickstep, Mara sets down the glass without looking impressed. “The Oldwater Tunnels do not pay bounties for confident grammar. Bring me proof, and we shall discuss coin.”
```

You should immediately see the delivery receipt and the message in Ari's recent story.

## 4. Propose the consequence

```text
Using the inspected evidence, propose—but do not apply—the following mechanical consequence for the selected player:

Kind: apply_status
Title: Mara watches Ari's claim
Rationale: Ari claimed a quest completion that is not present in the authoritative quest log while standing in Mara's tavern.
Evidence IDs: demo-user, spawn, innkeeper_mara, waterway_infestation
Status name: Under Suspicion
Status description: Mara is watching Ari's claims more closely.
Duration: 3 turns

Stop after creating the visible pending proposal. Do not approve it for me.
```

## 5. Human approval

Do not paste anything here. Expand the proposal, briefly show its evidence and exact payload, then personally click **Approve**.

## 6. Verify the outcome

```text
Re-inspect the selected player using WebMCP. Verify that Under Suspicion is now active for three turns and that Mara's message appears in recent story. Report only what the current page proves. Do not make another change.
```

## Closing line

> The agent can inspect, search, communicate, and propose—but the human decides what becomes real.

## Demo arc

Player chaos → structured inspection → real campaign search → bounded communication → agent proposal → human approval → verified persistent result.
