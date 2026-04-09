# B05 — Narrator Retry Logic

**Category:** Blocker
**Effort:** Small
**Touches:** `GAE.Narrator/`, `GAE.Discord/DiscordBotService.cs`

## What

The narrator makes a single LM Studio call per action. If it times out or errors, it immediately falls back to mechanical text. A momentary hiccup (LM Studio swapping models, GC pause, etc.) shouldn't dump the player into `"You see: [room description]"` mode.

## Why This Blocks MVP

LM Studio running locally is not as stable as a cloud API. Brief hiccups are normal. Without retry, players will regularly see ugly fallback text even though the narrator would have worked 500ms later. It makes the game feel broken.

## Acceptance Criteria

- [ ] At least 1 retry with a short delay (e.g., 1-2 seconds) before falling back
- [ ] Retry count and delay configurable in settings
- [ ] Total timeout still bounded (don't let retries stack to 30+ seconds)
- [ ] Fallback still works if all retries fail
- [ ] Narrator failure tracking (`_narratorFailures`) updated correctly with retries
- [ ] Test: transient failure → retry → success (mock the HTTP client)
- [ ] Test: persistent failure → retries exhausted → fallback triggered

## Notes

- Current timeout is 10s per action type. With 1 retry at 2s delay, worst case is ~22s — still acceptable for a Discord bot.
- Could use Polly or a simple loop. Keep it simple.
- The 3-failures-in-5-minutes warning should count *final* failures, not individual retry attempts.
