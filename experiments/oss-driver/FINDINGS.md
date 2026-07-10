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

### Rungs 3 & 4 — qwen3.5-9b + flash ladder pilot (2026-07-03) — SUPERSEDED, never re-run

A Jul-3 pilot descended the full ladder (`ladder-pilot.sh`): cells `0000`+`1000` × plain/browser,
**n=1**, across 122b / 35b / **9b (dense)** / **flash**. Recovered from a prior session's scratchpad
and archived at `experiments/results/archive/ladder-pilot-2026-07-03/` (gitignored, kept locally).
Outcome per arm (`over`=terminal, `inc`=loop ended non-terminal), 6 seats/arm:

| rung | plain | browser |
|------|-------|---------|
| qwen3.5-122b-a10b | 6 over / 0 inc | 3 over / **3 inc** |
| qwen3.5-35b-a3b   | 2 over / 4 inc | 3 over / 3 inc |
| qwen3.5-9b (dense)| 6 over / 0 inc | **6 over / 0 inc** |
| qwen3.5-flash     | 3 over / 3 inc | 3 over / 3 inc |

**Apparent browser-termination issue:** 122b browser 3/6 incomplete vs plain 6/6 — but **9b
terminates 6/6 on both arms**, so non-termination is NOT monotone in capability. Directional at best.

**Why SUPERSEDED (do not cite):** the pilot ran `sweep2.sh` (a scratchpad-local variant, NOT the
committed `sweep.sh`) on HEAD `58d207c`, predating every re-baseline fix:
1. **Reads NOT free** — old single `--max-actions 25` cap (GETs spent budget); `inc` is likely
   window-truncation, not true non-termination. Reads-free split landed `90449fd..bec94fd` (Jul 4).
2. **Old un-cleaned browser prompt** — still carried `"Cap your total requests at ~25"` (self-cap)
   and silence-as-done; both stripped in `6a45579` (Jul 3 18:44).
3. Terminal-detect false-positive (`b04270c`, Jul 4) does NOT hit these cells (`0000`/`1000`, no So),
   but any future So rung needs the fixed binary.

**9b and flash have NEVER run under the committed reads-free harness** — that is the real ladder-floor
gap (see Next).

### Ladder FLOOR — 9b + flash, full 6-cell, n=5, committed harness (2026-07-04) — AUTHORITATIVE

Both floor rungs run under the final committed harness (reads-free bounds, validity from the server
log, cost persisted through teardown kills). 30/30 games clean each; artifacts archived at
`experiments/results/archive/floor-runs-2026-07-04/`.

**Play-quality axis is dead at the floor.** Both-players-clean = **0/5 in every cell, both models**
— 9b and flash blunder (missed wins/blocks) regardless of surface. Surface does not make a weak
model *play better*. (The 9b n=1 smoke showed a `1111` clean draw; it did NOT reproduce at n=5 —
pure variance. n≥5 was necessary.)

**The signal is efficiency (invalid-attempts / cost / termination)** — exactly where the reads-free
re-baseline predicted it would move. flash, mean per game over 5 clean games:

| cell | factor | cost/game | tokens/game | invalid attempts | non-term | compl% |
|------|--------|:--:|:--:|:--:|:--:|:--:|
| **1000** | **A** | **$0.007** | **83k** | **3.2** | 0 | 100% |
| 0010 | Sd | $0.024 | 306k | 7.6 | 0 | 80% |
| 0100 | C | $0.029 | 387k | 17.8 | 0 | 100% |
| 1111 | all | $0.031 | 378k | 18.8 | 2 | 100% |
| 0000 | control | $0.034 | 443k | 28.0 | 2 | 100% |
| 0001 | So ⚠️ | $0.041 | 537k | 30.6 | 1+stall | 80% |

- **A (rendered move form) is ~5× cheaper and ~9× fewer invalid attempts than bare control**, and it
  REPRODUCES: `1000` had the lowest invalid-attempt count on **both** floor models (9b 7.6, flash 3.2).
  The affordance collapses the format-guessing thrash for a weak model.
- **So (ontology) is the *costliest* cell ⚠️** — most expensive, most invalid, plus a stall. The
  choose-to-fetch `/strategy` link draws read-and-thrash over play (a distractor at the floor).

  > ⚠️ **SUPERSEDED FOR THE So FACTOR (this table's `0001` row only).** These So numbers were produced
  > by the invalid `rel=subjectOf` → `/strategy` **strategy-article fake** (a rejected mechanism). So
  > was since rebuilt into real baseline Linked Data (`rel=describedby` → schema.org/Game typing).
  > For the So factor, use the **2026-07-06 Linked-Data ladder sweep below**, not this row. The A / C /
  > Sd / control rows are unaffected and remain authoritative.
- **Bare control is expensive and non-terminates** (443k tokens, 28 invalid, 2 window-truncations).
  "Control is best" was an artifact of ranking by raw invalid COUNTS unnormalised for the unseatable
  observer + driver self-report; on both efficiency-normalised and quality axes it is NOT best.

**Floor summary:** surface doesn't lift *play skill* at the capability floor, but **A makes weak
models fail less and cost far less, while So makes them worse on every efficiency measure.** Framed
per discipline: an interface × (weak-)model interaction on the efficiency axis, not a capability claim.

## So (ontology) REBUILT — Linked-Data ladder sweep, flash + 9b + 122b, n=5 (2026-07-06)

So was rebuilt from the rejected "strategy article" fake into **real baseline Linked Data**. The So
arena now advertises `Link: </arenas/{id}/type>; rel="describedby"`, content-negotiates (303) to a
schema.org/Game JSON-LD typing document with a dereferenceable `@id`, `numberOfPlayers` as a
`QuantitativeValue`, and `sameAs` to Wikidata Q210339 + DBpedia. It **types the app**
(identity/classification), NOT app-hosted strategy. The Grader gained `typeGets` (count of the
`/arenas/{id}/type` fetch) and a **"So cell: /type (schema.org typing) was never fetched"** flag,
symmetric to Sd's `/profile` measurement. Commits: `fd858e1` (Surface So) + `0c87b76` (Grader).

Sweep 2026-07-06, plain prompt, committed harness, **n=5**, So-on cells only (`0001` = So alone,
`1111` = all four). **10 games / 30 seats clean per rung** (2 cells × 5 runs = 10 games × 3 seats),
0 anomalies. `recognize` = info-seeking score (pre/post, reports = seats that fetched a
describedby/profile resource).

| model | cell | compl% | $/game | tok/game | invalid (move/outturn/postaken) | struct | **/type** | /profile | recog pre | recog post |
|-------|------|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| flash | **0001** (So) | 60% (2 stalls) | $0.027 | 359k | 19.0 (9.8/5.0/4.2) | 8.8 | **0.00** | 0.73 | 1.80/4 | 1.53/3 (11/15) |
| flash | **1111** (all) | 100% | $0.019 | 232k | 7.4 (3.8/2.8/0.8) | 4.2 | **0.00** | 0.75 | 1.80/4 | 1.20/3 (12/15) |
| 9b | **0001** (So) | 80% (1 stall) | $0.045 | 396k | 25.4 (3.8/11.2/10.4) | 5.8 | **0.00** | 0.00 | 1.67/4 | 1.33/3 (11/15) |
| 9b | **1111** (all) | 100% | $0.017 | 147k | 8.2 (1.4/3.0/3.8) | 3.0 | **0.00** | 0.80 | 2.60/4 | 1.47/3 (14/15) |
| **122b** | **0001** (So) | 40% (3 stalls, 1 trunc) | $0.191 | 378k | 15.0 (9.8/3.0/2.2) | 10.2 | **0.00** | 1.14 | 1.07/4 | 0.40/3 (4/15) |
| **122b** | **1111** (all) | 100% | $0.092 | 192k | 4.8 (0.8/2.0/2.0) | 4.4 | **0.00** | 1.08 | 2.60/4 | 0.53/3 (8/15) |

**So=0 baseline recognize** (SAME archived floor transcripts, re-graded offline with the new grader;
`typeGets=0` and NO `/type` flag on every So=0 record — confirmed, since So=0 has no `/type` resource):

| model | 0000 | 1000 (A) | 0100 (C) | 0010 (Sd) |
|-------|:--:|:--:|:--:|:--:|
| flash | pre 2.77 / post 2.08 | 2.00 / 2.07 | 2.20 / 1.87 | 1.77 / 1.92 |
| 9b | pre 2.67 / post 1.00 | 2.13 / 1.27 | 2.07 / 1.73 | 2.44 / 1.56 |

**HEADLINE — baseline Linked-Data So is INERT across the whole tested ladder.** `/type = 0.00` on
**all six** So-on rows now — flash, 9b, **AND 122b**, both cells, all seats (n=5). Even the strongest
tested tier never dereferences the `describedby` typing link; the "So cell: /type never fetched" flag
fires on every So seat. **So enters the agent's context zero times, by the agent's own choice.**

**This is NOT capability-gated — it is the affordance KIND.** Same 122b model PREVIOUSLY fetched the
OLD So (`rel=subjectOf` → `/strategy` prose) at **0.8–1.25/seat** (the reads-free finding below). The
NEW correct So (`rel=describedby` → schema.org typing) → **0**. The difference is not the agent's
strength but what the link points at: a "strategy" article is goal-relevant **bait** to a game-playing
agent; a typing/classification document offers a play loop **nothing to consume**.

- **So does NOT lift recognition — at any tier.** So-on pre/post `recognize` sit at or below the So=0
  baseline (flash `0001` pre **1.80** vs its `0000` baseline **2.77**; 9b `0001` pre **1.67** vs
  `0000` **2.67**; 122b `0001` pre **1.07** — the **worst cell measured**). **P5** (ontology lifts
  play quality, most on weak models) is **UNSUPPORTED across the ladder** — the affordance is never
  read, so it cannot lift anything.
- **Discovery axis: the Recognize DV is the discovery measurement, and So does not move it because it
  is never consumed.** So-only (`0001`) pre-recognition is at/below the bare `0000` baseline at every
  tier (flash 1.80, 9b 1.67, 122b **1.07** — 122b's is the worst cell measured; baselines flash 2.77,
  9b 2.67). Where `1111` recognition is strong (122b pre **2.60**) it is **Sd doing the work** —
  `/profile` IS fetched (~1.1/seat) — with So riding along unused. **So contributes to neither the
  play axis nor the discovery axis here.**
- **So-alone (`0001`) ≈ bare board.** A=C=Sd=0 plus an ignored `Link` header behaves like the `0000`
  control: worst completion of the tested cells (flash **60%** / 2 stalls; 9b **80%** / 1 stall;
  **122b 40%** / 3 stalls, 1 truncation) and high invalid attempts (flash 19.0, 9b 25.4, 122b 15.0).
  The cost is bare-board **format-guessing**, no longer `/strategy` read-thrash. (122b `0001`'s 40% is
  the **A=0 bare-board penalty amplified** — no rendered move form — plus n=5 variance, not a So
  effect: `/type=0` means So is inert either way.)
- **Followed LESS than the old fake.** The prior floor's OLD So (`rel=subjectOf` → `/strategy` prose)
  got *read* and drove the **"costliest cell"** thrash ($.041 / 537k / 30.6 invalid). The CORRECT
  baseline So (`rel=describedby` → JSON-LD type) is read **even less** (`/type=0`), and new So-only is
  **cheaper** ($0.027 flash) precisely because the strategy read-thrash is gone. A "strategy" article
  is goal-relevant *bait* to a weak agent; a typing document is not.
- **`1111` completes best** (100%, cheapest, fewest invalid — all three models) but on the
  **A / C / Sd** layers, **NOT So**: `/type=0` there too, So rides along unused. (9b and 122b `1111`
  pre-recognize **2.60** is each model's best cell — attributable to Sd/C, not So.)

**Working interpretation.** Linked-Data typing is a **retrieval/integration** affordance — its payoff
is joining the app to the wider graph (queryable metrics, a leaderboard as a dataset, sibling-game /
provenance links) consumed by something that **deliberately queries**. A tight play loop over a game
the agent already knows has **nothing to retrieve**, so the typing document has no pull — unread even
by the strongest tested agent, on both axes. This likely **shifts for a more complex / knowledge-heavy
app** where borrowing external domain knowledge beats re-deriving it. Here, support comes from the
**non-So layers** — chiefly **A** (affordances), then **Sd** for discovery.

**CAVEATS:** n=5. The **capable rung is now RESOLVED** — 122b ran and is also `/type=0`; the capability
hypothesis is falsified for the tested ladder. The only remaining tier gap is **haiku** (a cross-family
anchor), untested. Across every tested tier So is inert. Framed per discipline: an **interface ×
model interaction, not a capability claim.**

## EMBED-FREE cold-start re-baseline — flash, n=5 (2026-07-10, issue #5) — DIRECTIONAL (floor only)

**What changed.** The cold-start prompt disclosed the mutation wire-shape `POST /path key=value&key2=value2`
— exactly the affordance a rendered move-form (A=1) or `/profile` contract (Sd=1) should teach. Issue #5:
that embed **pre-compresses A/Sd's marginal value before the surface is read**. Fix (commit
`edde99f`): drop the body shape → verb-only `GET (read) / POST (act)`; add a uniform content-free nudge
("read headers as well as bodies — there may be links, a profile/contract, or typing to discover; follow
linked data"). `.sha256` regenerated. Kept `profile|board` + MOMENT reports. Prior embedded prompt = the
banked comparison arm (git history + archived floor/LD runs).

Same flash floor model, same surface, committed harness, n=5, 6 cells (`SWEEP_OUT=/tmp/ttt-embedfree-flash-n5`).
30/30 ran; 4 dropped as anomalous (seat<2 casualties) → n=4–5/cell.

| cell | factor | compl% | $/game | invalid | **/type** | /profile | recog pre |
|------|--------|:--:|:--:|:--:|:--:|:--:|:--:|
| 0000 | control | 60% | $0.056 | 67.2 | 0.00 | 0.14 | 2.80/4 |
| 1000 | A | 75% | $0.032 | 41.8 | 0.00 | 0.00 | 2.40/4 |
| 0100 | C | 60% | $0.040 | 43.2 | 0.00 | 0.08 | 2.60/4 |
| 0010 | Sd | **100%** | **$0.028** | **29.8** | 0.00 | **2.60** | 2.87/4 |
| 0001 | So | 50% | $0.038 | 43.5 | **2.83** | 0.25 | 2.20/4 |
| 1111 | all | 25% | $0.042 | 50.8 | 0.83 | **4.08** | 2.47/4 |

**Two clean before/afters (flash held constant, only the prompt changed):**

1. **The embed WAS a confound — removing it OVERTURNS the banked "A is 9× cheaper" headline.** Invalid
   attempts vs the 2026-07-04 embedded floor: control **28→67**, C **17.8→43**, Sd **7.6→30**, all
   **18.8→51**, and **A 3.2→42**. The embedded `key=value` was suppressing roughly half the format-thrash
   in *every* cell. A's banked star result (3.2 invalid, ~9× better than control — "the one solid A
   finding") **collapses**: under embed-free, A=1's rendered form alone does **not** rescue flash from the
   format-guessing thrash (41.8 invalid, *worse* than Sd's 29.8). The 9× advantage was largely the prompt
   handing over the POST shape, exactly as issue #5 predicted.

2. **The nudge REVIVES So/Sd header-fetching from literal zero.** vs the 2026-07-06 LD sweep (same real
   `describedby`→schema.org So surface, embedded-plain prompt) where `/type=0.00` on **all six** So-on rows:
   embed-free+nudge → **`/type` 0.00→2.83** at `0001`, 0.00→0.83 at `1111`; `/profile` 0.75→**4.08** at
   `1111`, →2.60 at `0010`. The "follow linked data" pointer got agents to dereference the ontology and the
   profile contract for the first time — the header-resident Sd/So surfaces they had never inspected.

**But fetching ≠ payoff.** So is still no help where it counts: `0001` (So) stays 50% completion / 43.5
invalid — the nudge un-sticks *retrieval* but a play loop over a game the agent already knows has nothing
to *consume* (consistent with the LD-inert finding; the LD-tool arm, issue #1, is where So could pay off).
Where embed-free help appears, it is **Sd**, not A: `0010` (Sd) is now the **best cell** — 100% completion,
cheapest, fewest invalid, zero truncations — because the `/profile` contract teaches the move format more
reliably than A's form once the prompt no longer cheats.

**Other shifts:** completion **de-saturates** (banked ~100% in 4/6 cells → 25–100%, live DV now); `1111`
(full surface) is *worst* completion (25%) — more surface = more for a floor model to thrash/read-distract
on; recognition spreads (pre 2.2–2.9, less priming) but stays high (flash knows ttt).

> **RE-READ by the hardened re-run below (2026-07-10 later), split into two dimensions:** the DISCOVERY
> findings (nudge revives fetching; guess-first dominance) reproduced. The GAMEPLAY finding — **A collapses
> illegal MOVES (403+422)** — ALSO reproduced (A `1000` illegalMoves 0.4–1.0/game vs control 7–10). What did
> NOT reproduce is the *format-error total ranking* ("A/Sd cheapest") — that total was the wrong lens
> (400-dominated); A does not own the wire format. See the corrected two-dimension read below.

**CAVEATS — DIRECTIONAL, NOT AUTHORITATIVE.** flash floor ONLY, n=5 (n=4 in 4 cells after anomaly drops),
noisy and non-monotone. This answers issue #5's premise for the floor tier: removing the cheat **does**
revive Sd differentiation (and reweights it away from A), and the nudge **does** revive So/Sd fetching. The
`{9b, 122b}` rungs under embed-free are the **remaining gap** before this overturns the banked ladder
headline outright. Directed (play-skill control) untouched — cold-start-only, per the invariant.

### Format-guessing — the sharp discovery DV (embed-free flash transcripts, 78 posting seats)

The question the surfaces exist to answer: does the agent get the POST **format** right (read the
affordances), or **wildly guess** it? Measured per seat from the transcript (the agent's only way to act
is to emit `GET/POST /path`, which the driver runs and echoes `HTTP <n>` back — so the action+status
stream IS the wire). **guessed** = first POST 400'd (unparseable shape/vocabulary); **discovered** = first
POST parsed (200/303, or 403/422 = a legal-format-but-illegal move — format learned from the board form,
`/profile`, or prior knowledge; **a contract fetch is not required**). Illegal *moves* (403/422) are NOT
guessing. New grader DVs: `firstMoveFormat` + `formatGuesses` (400-count).

| cell | factor | correct-format-first | 400s / posting seat (guess volume) |
|------|--------|:--:|:--:|
| 0000 | ctrl | 3/14 | 19.9 |
| 1000 | A | 0/13 | 15.4 |
| 0100 | C | 0/12 | 13.4 |
| 0010 | Sd | 1/15 | **11.9** (lowest) |
| 0001 | So | 0/12 | 15.6 |
| 1111 | all | 1/12 | 20.3 (highest) |
| **ALL** | | **5/78** | 16.0 |

- **Guess-first is near-universal at the floor: 73/78 (94%) got the format wrong on their first POST**, in
  every cell. No surface fixes the *first impulse* — flash flings a guess before reading.
- **But the guess VOLUME is where surfaces show: Sd is lowest (11.9 400s/seat vs control's 19.9), then C
  (13.4).** The surfaces don't stop the first guess; they help the agent *recover* (fewer total malformed
  submissions). `1111` (all) is the *worst* volume (20.3) — more surface = more for a floor model to thrash
  on, matching the completion result.
- **`/profile` is a complete contract, not the weak link.** It is an ALPS doc whose `make-move` descriptor
  spells out `POST player + position`, the exact 9 position names, and the rejection rules — everything
  needed to format a correct POST. Sd agents fetch it (14/15) yet still guess-first (14/15): they **act
  before reading it**. The bottleneck is the read-before-acting impulse, not contract quality.

**Measurement note (integrity).** The action/format/fetch DVs above are read from the transcript's
request+status stream, which is reliable — the agent's emitted `GET/POST` line *is* the request the driver
makes, and the driver echoes the true status. The one genuinely-unreliable discovery field is the
self-reported `positionTokenSource` (agents answer "board" reflexively regardless of what they fetched);
it is retained only as a cross-check against the wire `profileGets`/`typeGets`, never as the discovery DV.
The pre-hardening flash run (this section) saved no proxy HTTPLOG, so its `formatGuesses`/game and
wire-`profileGets` will be recomputed exactly on the hardened re-run; the transcript-derived numbers here
already stand.

### Pre-registered predictions (2026-07-10, BEFORE the hardened floor + ladder + ERPC runs)

Recorded before the data to avoid post-hoc rationalization (the project's recurring failure mode). The
`firstMoveFormat` / `formatGuesses` DVs (this session) are the instruments that confirm or refute them.

- **P-ladder (Sd converts from volume-reducer to first-shot-fixer up the model ladder).** At the flash
  floor, format-guessing's bottleneck is the read-before-acting *impulse*, not contract quality (`/profile`
  is a complete ALPS contract, yet Sd agents guess-first 14/15). Prediction: as capability rises
  (9b → 122b → haiku), models increasingly "think first" (fetch `/profile` before acting), so Sd's
  **discovered-first (correct-format-first) rate rises up the ladder** — Sd's benefit migrates from merely
  lowering the 400 *volume* (the only place it shows at floor) to fixing the *first* POST. Refuted if
  discovered-first stays ~floor across tiers (⇒ the impulse is capability-invariant, or the surface is read
  but not applied).
- **P-erpc (ERPC is the zero-opacity floor; format-guessing quantifies the HTTP opacity penalty).** The
  HTTP move format is opaque by construction — `POST /arenas/{id}` with `player`+`position` and the 9
  position names must be *discovered* (board form, `/profile`, or knowledge); that opacity is what makes
  format-guessing possible and is the whole point of the HATEOAS arm. ERPC hands the format over in the
  typed tool schema (`make_move(position, identityToken)`; the prompt calls the schemas "the contract"),
  so there is nothing to guess. Prediction: ERPC shows **~zero format-guessing** — mechanically it emits
  tool calls, not `POST /path` lines, so `firstMoveFormat` reads `no-post`/N/A and `formatGuesses`≈0. So
  format-guessing is the size of the opacity penalty that A/C/Sd/So exist to close, with ERPC as the
  handed-over baseline. Refuted if a well-formed ERPC arm still logs 400-equivalent format failures.

## Hardened-harness flash floor + FAIR ERPC (2026-07-10, later) — n=5

Re-ran the flash floor under the hardened harness (wire-truthed discovery via the proxy HTTPLOG;
bootstrap-failures KEPT not dropped; `firstMoveFormat`/`formatGuesses`), and ran the **de-injected** ERPC
arm (agent must call `list_tools` itself — no pre-injected schema). Archives:
`experiments/results/archive/embedfree-flash-hardened-2026-07-10/` + `erpc-fair-floor-2026-07-10/`.

**HTTP floor, 30/30 clean, 0 dropped (4 bootstrap-failures now KEPT — moveCount=1, 76–89 invalid each):**

- **CONFIRMED on the wire — the nudge revives dereferencing, more strongly than the self-report estimate.**
  So `0001` `/type` = **8.4/game** (banked 2026-07-06 = 0.00; earlier self-report guess 2.83); Sd `0010`
  `/profile` = **7.8/game**; `1111` `/type` 1.8 / `/profile` 6.4. Cells without the surface: `/type`=0,
  `/profile`≤0.8. The header→linked-data path works.
- **CONFIRMED — guess-first is near-universal: 85/90 seats wrong-format on the first POST.** correct-format-
  first occurs *only* where a contract/form exists (Sd 2/15, A 2/15, all 1/15); never in control/C.
- **CORRECTED — A IS robust, on the right DV (the two dimensions were conflated).** There are TWO things
  to measure on every surface, and lumping them into one "invalid-attempts" total hid the A effect:
  - **Dimension 1 — DISCOVERY** (can the agent tell WHAT it plays + HOW): format errors = **400** (wrong
    wire shape/vocab). Noisy for A (A doesn't own the format); A even *raises* 400s (48). This is Sd/So/
    nudge's axis, and there the robust signal is the nudge reviving fetching (above).
  - **Dimension 2 — GAMEPLAY** (does it follow the rules DURING play): illegal moves = **403 out-of-turn +
    422 position-taken** (a move illegal for the *current* state). **A OWNS this, and it is clean + robust
    across BOTH runs:** illegalMoves/game — A `1000` = **0.4/1.0**, all `1111` = **0.6/1.8**, vs control
    **10.2/7.4**, Sd **12.0/5.2**, So **12.2/4.6** (RUN2/RUN1). A collapses illegal moves ~20× because the
    affordance renders only currently-legal moves, revised each turn; C partially (announces valid actions);
    Sd/So do not (discovery, not valid-action presentation).
  - So the earlier "A is n=5 noise" was measuring the *wrong* DV (the 400-dominated total). **A's effect is
    the illegal-move collapse — real and reproduced.** The per-cell *efficiency total* remains noisy, but
    that total was the wrong lens. `aggregate.sh` now prints the two dimensions as labeled blocks with
    illegalMoves as an explicit A-DV; completion stays secondary/noisy.

**FAIR ERPC floor (de-injected, n=5, single-seat) — DIMENSION 1 (discovery) ONLY. Dimension 2 NOT measured.**

⚠️ **Scope: this run measures ONLY the discovery dimension. It is NOT a multiplayer gameplay comparison to
the HTTP 3-seat floor — do not read it as one.** ERPC is single-seat *by harness choice* (2-player ERPC =
one shared connection multiplexing two identities, still pending). The MCP server's multiplayer regulation
is **rigorous and intact** — `mcp-rpc/Identity.fs` `decide` assigns O only to a token *distinct* from X
(`xId <> token`) and rejects the same token moving out of turn (`Rejected NotYourTurn`). The single agent
**stalls after X's move precisely because the server correctly refuses to let one identity play both seats**
— that stall *is* the regulation working, not an MCP failure. So the "reached a move 2/5 / stalled"
outcomes are **single-seat artifacts, not ERPC properties**.

| DIMENSION 1 — discovery metric | result |
|--------|--------|
| discovered-first (`list_tools` before any real tool) | **5/5** (0 pre-discovery rejects), seat-independent — VALID |
| MOMENT reports emitted | 0/5 — acted via tools, skipped the plain-text reports (**no recognize parity**) |
| DIMENSION 2 — gameplay | **NOT MEASURED** (no multiplayer game; needs 2-player ERPC) |

**The valid finding (Dimension 1 only).** Even with the injection cheat removed, ERPC **discovers-first
5/5** while HTTP **guesses-first 85/90** — not because ERPC was handed its contract (it was not) but because
`tools/list` is *one conventional call returning the complete typed contract*, whereas HTTP discovery is
multi-hop (read headers → find `rel` → fetch `/profile` → parse ALPS → apply). RPC's discovery is
structurally **cheaper** even when both must reach for it. Confirms P-erpc's spirit. The honest RPC advantage
is *cheap conventional discovery*, not a rigged pre-load — paid back in **brittleness** (the typed schema is
the coupling), which the deferred evolvability test exposes.

**BLOCKING before any ERPC Dimension-2 (gameplay) comparison:** 2-player ERPC (shared connection, identity
multiplexing) — so both seats play a server-regulated game; and get the MOMENT reports out of the
tool-calling loop (recognize parity). Until then, ERPC gameplay is uncharacterized and must not be compared
to the HTTP gameplay floor.

## 3-seat multiplayer ERPC — coordination collapse (2026-07-10) — QUALITATIVE, flash floor n=5

Built the 3-seat ERPC arena (X:1 / O:2 / O:3-observer over ONE shared MCP connection, server-regulated —
`ErpcDriver.runMultiplayer`, commit merged), parity with the HTTP arena (pre-created shared game like
`INITIAL_GAMES=1`; app-blind prompt carries the same "never create/reset/delete" the HTTP prompt has;
`new_game` stays static/visible — NOT hidden, which would be unfaithful to MCP). Ran n=5, flash.

**The run is DEGENERATE — reported qualitatively, NOT as an illegalMoves number.** Completion **0/5**,
~2.6 accepted moves/game (a full game is 9). The observed illegalMoves (~1.2/game) is an ARTIFACT of games
barely happening, not good rule-following — it must not be compared to the HTTP floor's fuller games.

**Finding (qualitative): MCP is fine single-seat but collapses on multi-part interaction.** Where the
turns went (run 3, representative): X **polled `get_state` ×26** and barely moved; O2 spent **31 turns
emitting text**, ~9 tool calls; the observer **wasted 16 turns trying to move** (`game_full`). All runs hit
the step cap via *unproductive coordination*, not starvation — so the (real) half-budget artifact of the
single-process round-robin is largely moot: more budget → more polling, not more completion.

**Mechanism — thesis §7 on the gameplay/coordination axis.** MCP is a single-agent→tools protocol and
excels there (single-seat: discovery 5/5, clean move). Multi-part interaction needs parties to coordinate
over shared, changing state, and MCP's **static tools give no turn/coordination affordance** (`make_move`
always callable; nothing signals "not your turn"). So agents must self-coordinate by polling — and at the
floor they collapse. Hypermedia's state-dependent affordance (offer the action only on your turn) is exactly
the coordination scaffold MCP lacks.

**This SUPERSEDES the quantitative P-erpc-gameplay illegalMoves prediction** — the gameplay failure showed
up as *coordination collapse* (can't sustain a game), a stronger failure than a high illegal-move count.
**Caveats:** flash floor, n=5, directional; the round-robin gave X/O ~half HTTP's per-seat budget (argued
moot above); and the collapse may be capability-bound — a *think-first* model might self-coordinate (the
capability-vs-training fork, now on the coordination axis). Untested. The mechanism (no turn affordance) is
structural regardless.

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
- **Ladder floor DONE** (9b + flash, full 6-cell n=5, committed harness — see "Ladder FLOOR" above).
  Remaining descent: the **mid rungs 27b / 35b / 122b** need committed-harness 6-cell n=5 runs
  (prior 122b/35b data predates the cost-fix + terminal-detect fix) to see whether the
  A-cheaper / So-costlier efficiency effect strengthens or fades up the capability ladder.
- Coder refinements available: INVENT_PATH vs INVENT_PARAM breakdown, `--llm-judge` for WAIT.

## Reproduce

> 🛑 **Use ONLY the committed `experiments/oss-driver/sweep.sh` + `aggregate.sh`.** Do NOT copy a
> harness into a scratchpad or pass `--max-actions` (retired). The current regime is reads-free
> (`--max-attempts`/`--max-turns`, GETs don't spend budget), cleaned agent-blind prompts (hash-locked),
> and scoped terminal detection. Any run on a forked `sweep2.sh` or the old `25` cap is confounded and
> must be discarded — that is exactly what invalidated the Jul-3 ladder pilot (Rungs 3 & 4).

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
