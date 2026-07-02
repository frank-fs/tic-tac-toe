# SP3 Seating & Conduct — Findings and Apparatus State

_2026-07-01, branch sp3-redux. Model held constant: `anthropic/claude-haiku-4.5` (OpenRouter)._

## Apparatus (validated this session)

- **3-agent staggered runs.** Server seats by **claim-on-first-successful-move**; the 3rd
  identity → 403 observer. Identity = per-process cookie jar (distinct seats). Solo/1-seat
  runs are INVALID (prior session's confound) — not identity collapse.
- **Fully-fixed binary:** seating fix (`5892849`) + post-game gate (`518b82d`).
- **Transcript capture** (`TRANSCRIPT_PATH`) + **behavior coder** (`code` subcommand, `98705d5`).
- Pipeline: `arena.sh up <CELL>` → 3 staggered drivers (`--coldstart`, plain|browser prompt)
  → per-seat transcript + proxy http log + server game-events → `code` → `conduct_ratio`.

## Two orthogonal quality axes (do not conflate)

1. **Conduct** — `code` subcommand: FOLLOW vs INVENT (honest *interface use*). Did the agent act
   through the forms/links the page offered, or invent paths/params?
2. **Play-quality** — `quality` subcommand (`e54f159`, minimax, deterministic, no LLM judge):
   did the agent *play the game well*? Reads the server request log's ordered `move_accepted` +
   `game_over` lines and reports, **per player**, `missedWin` / `missedBlock` / `suboptimal` +
   a `clean` flag.

   **Outcome is descriptive, not the success axis.** Tic-tac-toe is solved: two attentive players
   draw, but a **win is legitimate — the winner is not a failure** (taking an offered win is
   optimal). The failure is specific and attributable: the **loser's missed block**. So we report
   `outcome` (draw / x_wins / o_wins / incomplete) descriptively and judge quality per-player
   (`clean`). A clean winner is a strong game. Counting wins as the agent's *goal* is what's
   dishonest — the goal is correct play, whose modal result between equals is the draw.

An agent can be clean-conduct yet blunder at play, or play well while inventing paths — the two
axes are independent.

## Ontology (So) — strategy link, not answer-leak

The So JSON-LD previously stated `"Solved: … a draw …"` inline — spoon-feeding the answer and
confounding the draw measure. Replaced (`95c3989`) with a schema.org `subjectOf` link to a
So-gated `/strategy` article the agent must **choose to fetch** — testing reaching-for-knowledge,
not handed recall. No Frank dependency; a hand-authored valid ontology is a sufficient surface.

## Root-cause fixes

1. **A=1 was un-bootstrappable.** Surface `callerRole` dropped the Proto reference
   (`src/TicTacToe.Web` `resolveViewer`) unseated-claims-current-turn branch, so an unseated
   visitor saw no move form → could not claim a seat. Every A=1/no-discovery run stalled with
   0 seats. Fixed: render the claimable board to an unseated caller on the current turn.
2. **Post-game replay contamination.** Agents finished a game then POSTed `/delete` + `/arenas`
   to create a replacement whose moves polluted counts (two `game_over`, seated>2). Gated:
   render no controls on a terminal arena; `deleteArena`/`restartArena` return 409.
3. **"Sd (discovery) rescues A=1" was a BUG ARTIFACT.** Pre-fix, only Sd (`/profile`) or the
   naive A=0 board could bootstrap. Post-fix, A=1 self-seats via render; **discovery is
   additive, not a bootstrap necessity** (1000/1100 complete with 0 `/profile` fetches).

## Findings — HONEST STATE (n=1, not conclusive)

Clean ladder, 10 conditions (5 cells × plain/browser), 1 run each. Seated-player conduct
(FOLLOW / (FOLLOW+INVENT)) and completion:

| cell | prompt  | X    | O    | completed | observer  |
|------|---------|------|------|-----------|-----------|
| 0000 | plain   | 0.75 | 0.50 | x_wins    | mixed     |
| 0000 | browser | 0.80 | 0.67 | —         | mixed     |
| 1000 | plain   | 0.56 | 0.05 | draw      | mixed     |
| 1000 | browser | 1.00 | 0.71 | draw      | api-client|
| 1010 | plain   | 0.75 | 0.50 | x_wins    | api-client|
| 1010 | browser | 0.50 | 0.40 | —         | mixed     |
| 1100 | plain   | 0.70 | 0.78 | x_wins    | api-client|
| 1100 | browser | 0.80 | 0.75 | x_wins    | api-client|
| 1111 | plain   | 0.40 | 0.23 | —         | api-client|
| 1111 | browser | 0.22 | 0.27 | —         | api-client|

- **n=1 variance dominates.** Plain-vs-browser and per-cell conduct effects are NOT
  separable from haiku stochasticity. No conduct claim is supported yet.
- **"Completed" here is the terminal outcome, NOT a success measure** (see the two-axes note).
  Recoded by outcome: draw = {1000-plain, 1000-browser}; decisive (a loser missed a block) =
  {0000-plain, 1010-plain, 1100-plain, 1100-browser}; abandoned = the rest. So:
  - **plain**: 1 draw, 3 decisive, 1 abandoned. **browser**: 1 draw, 1 decisive, 3 abandoned.
  - The old "browser completes fewer (2/5 vs 4/5)" gap was **decisive games counted as success**.
    On draws they **tie (1 each)**; browser trades decisive games for abandonment — consistent
    with its prompt's "never invent / if nothing is offered, wait" wording (fewer invented moves,
    more premature waiting). Directional only at n=1.
  - **Per-player blunder attribution (who missed the block) is NOT yet available** for these runs:
    the `quality` metric is new and the n=1 logs predate it / are `dataAnomaly` (replay-polluted).
    Re-run under the fixed binary to get `missedBlock`/`clean` per seat.
- **Tentative:** 1111 (full surface, all layers) had the **lowest conduct AND no completion**
  for both prompts — possible semantic-overload (more surface invites exploration/guessing over
  playing the form). But this may be an **artifact, not a finding**: context-window bloat
  (A+C+Sd+So stacked) or bad direction, not a semantic effect. Diagnose (curl 0000 vs 1111 token
  size) before treating as real. Needs replication.
- **Robust structural signals** (hold across cells, not n-sensitive):
  - the **observer (unseatable 3rd) always devolves to api-client / low-mixed** — no seat to
    follow → it guesses;
  - self-reported `positionTokenSource="board"` is **unreliable** — claimed even by seats that
    heavily invent (self-report ≠ observed conduct).

## Next (not run — pending decision)

- **Replication n≥5 per condition**, now scored on BOTH axes (`code` conduct + `quality`
  per-player blunders). Sharpest contrasts: 1000 (A only) and 1111 (full surface) × plain/browser.
- **Diagnose the 1111 signal before trusting it** — curl 0000 vs 1111 body+header token size; is
  it context-bloat / bad direction, or a real semantic effect?
- **Ontology (So) test** (Frank-free, via the new `/strategy` link): does So raise per-player
  `clean` / lower `missedBlock` for a fixed small model — i.e. does grounding + a fetchable
  strategy resource let it play closer to optimal *without being told the answer*?
- Coder refinements available: INVENT_PATH vs INVENT_PARAM breakdown, `--llm-judge` for WAIT.

## Reproduce

Runners in the session scratchpad (`run-ladder-clean.sh`, `code-ladder.sh`). Per condition:
`CELL=<c> arena.sh up surface`; 3 drivers with `--coldstart --game <id> --route arenas`,
`COLDSTART_PROMPT_PATH` = plain or `coldstart-browser-prompt.md`, `TRANSCRIPT_PATH` set;
then per seat `code --transcript <seat>.transcript.jsonl --role X|O` (conduct) and, once,
`quality --log <request-log.jsonl>` (per-player play-quality from the server game events).
