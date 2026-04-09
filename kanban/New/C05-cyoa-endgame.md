# C05 — CYOA: Endgame & Story Conclusion

**Category:** Feature — Choose Your Own Adventure Mode
**Effort:** Small-Medium
**Touches:** `GAE.Engine/`, `GAE.Narrator/`, Discord output

## What

The narrator needs a way to signal "the story is over." Victory, tragedy, cliffhanger — then the session wraps up cleanly.

## Depends On

- **C02** (Choice Tree) — ending is the final node
- **C03** (Save Points) — ending should be a terminal state, no rewind

## Design

### Ending Signals
- Narrator includes an `"ending"` flag in its structured output: `"ending": "victory"`, `"ending": "tragedy"`, `"ending": "cliffhanger"`
- Engine detects the flag and transitions to endgame state

### Endgame Flow
1. Narrator's final narration displayed (the ending scene)
2. No choices offered — story is over
3. Adventure summary shown:
   - Total choices made
   - Nodes visited
   - Items collected
   - Deaths / rewinds
   - Ending type
4. Player asked: "Play again?" or returned to normal state
5. CYOA session data archived (or cleared)

### Narrator Ending Triggers
- Narrator decides organically when to end based on story arc
- Engine can hint: "This is node 25 of ~30. Start wrapping up." (via prompt context)
- If max nodes reached without narrator ending, engine prompts: "Bring the story to a conclusion in the next 1-2 nodes"

## Acceptance Criteria

- [ ] Narrator can signal ending with type (victory/tragedy/cliffhanger/open)
- [ ] Engine detects ending flag and stops offering choices
- [ ] Final narration displayed
- [ ] Adventure summary generated and shown to player
- [ ] Session cleaned up — player returned to normal state or offered replay
- [ ] Forced ending if narrator hasn't concluded near max node count
- [ ] Test: narrator signals ending → session concludes
- [ ] Test: max node forced ending works

## Notes

- The summary is a nice touch that makes the experience feel complete. Even simple stats ("You made 23 choices and died twice") give closure.
- Consider saving the full choice history so the player could theoretically replay or share their story.
