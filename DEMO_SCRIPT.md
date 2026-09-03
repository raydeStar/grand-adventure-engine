# Grand Adventure Engine: Co-DM — 2:20 Demo Script

## 0:00–0:15 — The product

**Visual:** Grand Adventure Engine title, then the seeded Player Flow.

**Voiceover:** “Most AI RPGs ask the model to remember and enforce an entire world inside the chat. Grand Adventure Engine already makes the engine authoritative. For the WebMCP Challenge, I added an AI Co-DM that works beside the human Dungeon Master.”

## 0:15–0:35 — The unexpected claim

**Visual:** In Player Flow, Ari Quickstep stands in The Lantern's Rest. Enter: “I tell Mara I already cleared the Oldwater Tunnels and demand the reward.” Keep the resulting story visible.

**Voiceover:** “Ari claims a quest reward. The Co-DM should not trust the claim—or invent what Mara knows.”

## 0:35–1:10 — Structured inspection

**Visual:** Switch to the Admin Console's DM Console. Select Ari in the AI Co-DM panel. Show WebMCP Status with five registered tools. Submit the prepared browser-agent prompt.

Show, in order:

1. `get_dm_context` and the visible Current Scene;
2. `search_world` for Mara and the existing DM result cards;
3. `inspect_entity` opening Mara in the existing detail panel;
4. `search_world` and `inspect_entity` for The Waterway Infestation.

**Voiceover:** “WebMCP gives the signed-in browser agent structured access to the same state and search tools the human DM sees. Ari is at `spawn`, Mara is present, and the real quest definition exists—but Ari's authoritative quest log does not show that completion.”

## 1:10–1:35 — Grounded response

**Visual:** Show `send_dm_message`, its Agent Activity receipt, and the message appearing in Player Flow.

**Prepared message:** “Ari Quickstep, Mara sets down the glass without looking impressed. ‘The Oldwater Tunnels do not pay bounties for confident grammar. Bring me proof, and we shall discuss coin.’”

**Voiceover:** “The agent sends a bounded message to exactly one player. It persists in the browser story even with Discord disabled.”

## 1:35–2:05 — Proposal and human decision

**Visual:** Show `propose_dm_intervention` create “Mara watches Ari's claim.” Expand its evidence and exact payload. Pause on `pending`, then click **Approve** yourself.

Show the card become `approved`, the human-decision receipt, refreshed Current Scene, and `Under Suspicion` in Ari's Player Flow state.

**Voiceover:** “The agent may propose a persistent consequence, but it cannot apply one. The human reviews the evidence and exact payload. Only this click invokes the existing validated status API.”

## 2:05–2:20 — Close

**Visual:** Frame Current Scene, Agent Activity, approved proposal, and WebMCP diagnostics together. Briefly flash the Player Flow final state.

**Voiceover:** “WebMCP turns the AI from an improviser guessing at the interface into a Co-DM with structured access to the same living world, while the human remains in control.”

## Recording notes

- Use the built-in GAE Player Flow and Admin Console as the two visible surfaces.
- Keep Discord branding and tokens off-screen; Discord is not required.
- Do not show `.env`, cookies, credentials, connection strings, API keys, or narrator prompts.
- Use no copyrighted background music.
- Target 2:20; cut tool-call waiting before cutting evidence or the human approval click.
