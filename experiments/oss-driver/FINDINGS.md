# SP3 Seating & Conduct — Findings and Apparatus State

_2026-07-01, branch sp3-redux. Model held constant: `anthropic/claude-haiku-4.5` (OpenRouter)._

## Factor-isolation sweep — n=5, haiku, 60 games incl. 0000 control, 0 anomalies (2026-07-03)

Cells 0000 (control) + 1000/0100/0010/0001/1111 (4 single-factor + all-on) × plain/browser × 5.
Hardened harness (game-lock, bounded waits, bulletproof teardown, single-instance guard, artifact
aggregation). Completion = a terminal outcome (draw or decisive); draws stay rare at this tier, so
completion — not draw-rate — is the discriminating measure. **The 0000 control is load-bearing: it
inverts the naive reading.**

| cell | factor | plain | Δ vs control | browser |
|------|--------|-------|-------------|---------|
| **0000** | **control (none)** | **80%** | — | 20% |
| 1000 | A affordances | 80% | 0 | 60% |
| 0100 | C accessibility | 60% | **−20** | 20% |
| 0010 | Sd discovery | 60% | **−20** | 20% |
| 0001 | So ontology | 40% | **−40** | 0% |
| 1111 | all four | 100% | **+20** | 40% |

**Signal 1 — super-additive interaction, NOT main effects (this is the hypothesis's headline).**
Against the 80% bare baseline: **isolated semantic factors HURT** (C −20, Sd −20, So −40 — a lone
discovery/ontology layer is a distractor: the agent reads it and thrashes instead of playing).
**A alone is neutral** (haiku already handles a plain HTML form). **Only the full stack (1111)
beats control** (+20). The layers help *together*, not individually — exactly the pre-registered
prediction ("all layers together, by large margins; not a main effect"). Without 0000 the earlier
"affordances dominate, more is better" read looked true and was WRONG.

**Signal 2 — the browser-conduct prompt is REJECTED (decision, 2026-07-03).** Browser completion is
lower than plain in ALL 6 haiku cells (0000: 80→20). Confirmed down the Qwen ladder (pilot, n=1,
0 anomalies): browser NEVER beats plain at any tier —

| tier | 0000 plain | 0000 browser | 1000 plain | 1000 browser |
|------|:--:|:--:|:--:|:--:|
| 122b | ✓ | ✗ | ✓ | ✓ |
| 35b-a3b | ✓ (draw) | ✗ | ✓ | ✓ |
| 9b | ✓ | ✓ | ✓ | ✓ |
| flash | ✓ | ✗ | ✓ | ✓ (draw) |

Plain 8/8 complete; browser 5/8, every failure on the bare 0000. The premise ("a weak model needs
interface-conduct guidance") did NOT hold — even flash completes with plain. The browser prompt's
"keep polling / waiting is fine / don't invent" makes agents FREEZE on the bare surface instead of
figuring out the naive board (1000's forms carry browser through; 0000 has none). **Decision: drop
the browser prompt (coldstart + directed). The cold-start instrument is the plain prompt.**

**Caveats:** n=5 haiku / n=1 Qwen (directional); completion not draw-rate; some browser cells lost
token data (completion still valid from the server log).

## Model ladder (next)

Browser dropped → the ladder runs **plain-only**. Descend Qwen3.5 with the full cell set incl. the
0000 control: `qwen/qwen3.5-122b-a10b` → `qwen3.5-35b-a3b` → `qwen3.5-9b` → `qwen3.5-flash-02-23`.
Question: does the super-additive pattern (singles ≤ baseline, full > baseline) hold, strengthen,
or shift as the model weakens?

### Rung 1 — qwen3.5-122b-a10b, plain, n=5, 30 games, 0 anomalies (2026-07-03)

| cell | factor | 122b plain | Δ vs control | haiku plain | haiku Δ |
|------|--------|:--:|:--:|:--:|:--:|
| **0000** | **control** | **80%** | — | 80% | — |
| 1000 | A affordances | 100% | **+20** | 80% | 0 |
| 0100 | C accessibility | 80% | 0 | 60% | −20 |
| 0010 | Sd discovery | 80% | 0 | 60% | −20 |
| 0001 | So ontology | 40% | **−40** | 40% | −40 |
| 1111 | all four | 100% | +20 | 100% | +20 |

**The super-additive-interaction headline does NOT reproduce at 122b.** Both hypotheses break:
*singles ≤ baseline* FAILS (A alone → 100%, +20, a positive main effect) and *only-1111 > baseline*
FAILS (1000 and 1111 both hit the 100% ceiling). At this surface the haiku-tier pattern resolves
into **an A main-effect (+20) plus a persistent So-alone penalty (−40)**, with C/Sd going neutral.

What held across both tiers: **So-alone = −40 identically** (ontology-alone is a model-independent
distractor — the choose-to-fetch `/strategy` link draws read-and-thrash instead of play) and
**1111 = 100%** (full stack ceilings out). What moved: the semantic/affordance singles rise
(C, Sd −20→0; A 0→+20) — isolated layers stop hurting except So. Framing per experiment discipline:
this is an **interface × model interaction**, not a capability claim about either model.

### Rung 2 — qwen3.5-35b-a3b, plain, n=5, 30 games, 0 anomalies (2026-07-03)

| cell | factor | haiku | 122b | 35b | 35b Δ ctrl |
|------|--------|:--:|:--:|:--:|:--:|
| **0000** | **control** | 80 | 80 | **80** | — |
| 1000 | A affordances | 80 | 100 | 80 | 0 |
| 0100 | C accessibility | 60 | 80 | 80 | 0 |
| 0010 | Sd discovery | 60 | 80 | 60 | **−20** |
| 0001 | So ontology | 40 | 40 | 20 | **−60** |
| 1111 | all four | 100 | 100 | 80 | 0 |

**At 35b the surface goes inert.** *singles ≤ baseline* HOLDS (nothing exceeds 80; A's 122b lift is
gone); *only-1111 > baseline* FAILS — 1111 drops to 80 = control. Every factor is neutral-or-harmful
and the full stack no longer rescues.

**Ladder trend (surface weakening haiku → 122b → 35b):** the union's advantage **decays**
(1111: 100 → 100 → 80) and the ontology-alone penalty **deepens** (So: −40 → −40 → −60,
monotonic). Sd reverts to −20; A's +20 is a 122b-only blip (0 → +20 → 0, non-monotonic). Directional
read: as the model weakens the discovery/ontology surface stops paying rent and the semantic layers
drag — value decays toward "just let it play bare." Neither a clean hold nor a clean inversion of the
haiku super-additive pattern: **decay.**

> ⚠️ **SUPERSEDED — Rungs 1 & 2 above are CONFOUNDED (see the 2026-07-04 correction below).** The
> completion differences that drove the "super-additive / singles-hurt / So-alone-distractor" reading
> were artifacts of three stacked measurement bugs, not surface effects. Corrected → completion
> saturates at 100% across the whole factorial. Kept for the audit trail; do not cite as results.

## CORRECTION — reads-free / agent-blind re-baseline (2026-07-04, branch `reads-free`)

Re-ran the plain ladder (122b + 35b) after fixing three confounds discovered this session. **The
banked "super-additive interaction" headline does not survive. It was measurement artifact.**

**The three confounds (all removed):**
1. **~25 request budget, doubly imposed.** The 25-action harness cap AND a hardcoded
   "Cap your total requests at ~25 / if the server stops responding the app is finished, stop"
   clause in BOTH the plain and browser cold-start prompts. Completion was budget-gated, not
   ability-gated — every incomplete was a seat cut off mid-play, not a stall. Fixed: agent-blind
   observation windows (reads free — GETs don't spend the mutation budget; two generous invisible
   bounds `MaxAttempts=30`/`MaxTurns=80`), and the self-cap/silence-as-done clauses stripped from
   both prompts (re-locked).
2. **So ontology was inert clutter.** Inline JSON-LD re-injected every turn; agents never followed
   its `/strategy` link (0 fetches observed). Fixed: So advertises `/strategy` via a `Link` header
   (the channel agents DO follow — like Sd's `/profile`); inline JSON-LD dropped.
3. **Terminal false-positive on the strategy article.** Once (2) made agents actually fetch
   `/strategy`, its prose ("…that wins.") tripped the driver's loose `" wins"` game-over token on a
   NON-game path → the seat quit with ~0 moves → the game logged incomplete. Dose-response proved it
   (122b fetched /strategy 1.0/seat → 0001 "completed" 0%; 35b 0.53/seat → 60%). Fixed: terminal
   token detection scoped to the game resource path only.

**Corrected plain completion — 100% in EVERY cell, BOTH tiers** (n=4–5/cell, seat-truncations=0,
seats terminate in ~5–8 turns, far under the 80-turn backstop):

| cell | factor | 122b | 35b | banked (confounded) |
|------|--------|:--:|:--:|:--:|
| 0000 | control | 100 | 100 | 80 / 80 |
| 1000 | A | 100 | 100 | 100 / 80 |
| 0100 | C | 100 | 100 | 80 / 80 |
| 0010 | Sd | 100 | 100 | 80 / 60 |
| 0001 | So | 100 | 100 | 40 / 20 |
| 1111 | all | 100 | 100 | 100 / 80 |

**Completion is no longer discriminating** — it saturates. The signal moves to the axes flagged as
primary: **invalid-interaction attempts, cost/tokens, and info-seeking** (mean/seat):

| cell | 122b invalid | 35b invalid | 122b $ | 35b $ | /strategy | /profile |
|------|:--:|:--:|:--:|:--:|:--:|:--:|
| 0000 | 3.6 | 8.0 | .046 | .038 | 0 | ~0.15 |
| 1000 (A) | **1.0** | 6.0 | .060 | .030 | 0 | 0 |
| 0100 (C) | 5.5 | 8.4 | .081 | .039 | 0 | ~0.3 |
| 0010 (Sd) | 5.0 | **12.4** | .065 | .066 | 0 | 1.1–1.8 |
| 0001 (So) | 8.0 | 8.6 | .079 | .043 | **0.8–1.25** | ~0.3 |
| 1111 (all) | **2.2** | **2.8** | .076 | **.029** | ~0.33 | 1.5 |

**What survives, reframed as EFFICIENCY not completion:**
- **Affordances (A/1000) and the full stack (1111) yield the cleanest, cheapest interaction** —
  fewest invalid attempts (1000: 1.0 at 122b; 1111: 2.2/2.8 both tiers; 1111 cheapest at 35b, .029).
  Forms structure the interaction so the agent mis-submits less.
- **Isolated semantic layers (Sd, So) cost more** — most invalid attempts + tokens (Sd 12.4 at 35b,
  most expensive; So 8.0–8.6) — agents explore/read/mis-submit more. They now COMPLETE, but less
  efficiently. So the old "semantic-only hurts" is real only as an *efficiency* penalty, not a
  completion failure.
- **35b thrashes ~2× 122b** across cells (weaker model, more invalid attempts) — but completes.

**D2 validated:** the `Link`-header So gets fetched (0 → 0.8–1.25 `/strategy`/seat in 0001);
`/profile` fetched 1.1–1.8/seat in Sd cells. Agents act on advertised links, ignore inline JSON-LD.

**Browser arm — inconclusive on conduct, but a mechanism finding.** Under agent-blind (self-cap
removed), the "keep polling / waiting is fine" browser prompt makes agents never self-terminate →
they run to the 400s per-game wall → killed mid-write → dropped as `seatCrash` (122b browser: 13/30
dropped; sweep took 2h18m vs plain's 65m). **The browser prompt's prior "completion" depended on the
very self-cap that made it agent-aware.** A fair browser re-test needs a termination fix first
(e.g. the driver detecting a stable/terminal board), not the wall-kill. Deferred.

**Bottom line:** with a fair, agent-blind, reads-free apparatus, all four affordance/semantic layers
let a mid/low-tier open model complete cold-start tic-tac-toe (100%). The discovery layers earn their
place on **interaction efficiency and honest info-seeking**, not on a completion gap — the completion
gap was our instrument, not the surface. n small (4–5/cell); efficiency deltas are directional.

---

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

Committed harness (hardened: game-lock, bounded waits, bulletproof teardown, single-instance
guard, artifact-only aggregation):

```bash
# one tier, full cell set incl. 0000 control, plain prompt, n=5
MODEL=qwen/qwen3.5-122b-a10b SWEEP_OUT=/tmp/qwen-122b \
  bash experiments/oss-driver/sweep.sh
bash experiments/oss-driver/aggregate.sh /tmp/qwen-122b   # -> clean.jsonl + completion% per cell
```

`sweep.sh` runs `CELLS x RUNS` of 3-seat cold-start games (server seats first two, 3rd observes),
one server per game, and writes raw `q-`/`s-`/`c-` artifacts per game; `aggregate.sh` rolls them
up. Env: `MODEL`, `CELLS` (default all 6), `RUNS` (default 1-5), `SWEEP_OUT`. Per game it records
`quality` (per-player play-quality from the server event log) and `code` (FOLLOW/INVENT conduct).
Model ladder: rerun with `MODEL=` down the Qwen tiers.
