# Discord Release Playtest

This is the final human-client check for a release candidate. It deliberately uses a real Discord user because a bot
cannot generate a trustworthy inbound user message to itself. Allow 15-20 minutes and use a disposable test character.

## Preconditions

- The application reports `healthy` from `/health`, `/health/db`, and `/health/narrator`.
- Startup logs contain `Discord bot connected`, `Ready`, and `Slash commands registered`.
- The test user can see the configured adventure channel and the bot appears online.
- If testing Luna, record the model and reasoning effort from `/health/narrator` before the first command.

## Player path

Record a screenshot or message link after each numbered checkpoint.

1. Run `/help`; confirm Discord acknowledges the command once and shows the current command set.
2. Run `/create` and finish character creation; confirm a private player thread is created and the introduction names
   the character.
3. Send `look`; confirm one narration response appears and no room metadata dump leaks into the story.
4. Speak to an NPC for two turns, then walk away; confirm conversation mode engages and the NPC's disposition change
   remains visible when speaking again.
5. Travel west from the Back Alley into Moonfall Fair; confirm the newly seeded district is reachable without an admin
   reset.
6. Accept `The First Bell`; complete its discover, talk, and return steps; confirm rewards are granted once.
7. Start one combat, take one combat action, then flee; confirm damage/state changes persist after another command.
8. Run `/stats`, `/inventory`, and `/map`; confirm each response is sent once and reflects the preceding actions.
9. Restart the application without deleting volumes; confirm the character, Moonfall progress, NPC disposition, and
   player thread association survive.
10. Send one final `look`; confirm the bot resumes in the correct room and world.

## Server evidence

For the same session, retain:

- Application logs covering Discord ready state, each command correlation, and restart.
- The narrator health payload and observed response latency range.
- Database counts for the test player's story entries, quest progress, and player-room records before and after restart.
- Any Discord message links or screenshots, with tokens and private identifiers redacted before sharing.

The release passes only when every checkpoint has both a visible Discord result and corresponding server evidence.
Silence, duplicate replies, or an unverified restart is a failure - mystery is charming in a butler, less so in state
persistence.
