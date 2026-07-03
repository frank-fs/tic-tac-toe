# Reads-Free Observation Windows + So Discovery Channel — Design

_2026-07-03, branch `reads-free`. Test tier: `qwen/qwen3.5-35b-a3b` (most thrash + most cap-hits →
largest headroom to observe the change). Model discipline: OpenRouter slug held per run._

## Motivation (observed, this session)

Three coupled findings from the 122b + 35b plain sweeps (n=5, 0 harness anomalies):

1. **Completion is cap-gated, not play-gated.** Every incomplete game — both tiers — was a driver
   that hit the 25-action cap *while still playing* (6–8 of 9 moves), `actions=25/incomplete`. Zero
   genuine stalls. 22/60 seated 35b drivers hit the cap. The `--max-actions` loop counts **every**
   turn, including read-only GETs, so information-seeking is taxed against the same budget that
   bounds play.
2. **So (ontology) is inert — the penalty is clutter, not fetch-cost.** Across all 30 So-cell seats,
   agents emitted **0** `GET /strategy`. The JSON-LD is re-injected into **every** board observation
   (409 page-echoes) but never acted on. The −40/−60 completion penalty + thrash (19 invalid
   attempts/game at 35b) come from the unused blob diluting the observation each turn, not from
   following the `subjectOf` link.
3. **Agents DO use the header/`/profile` channel.** In Sd cells they fetched `/profile` 32 (0010) /
   18 (1111) times, **all 200**; in non-Sd cells only 0–4 speculative probes, all 404. `Link:` headers
   present 228×. Agents act on advertised links; they ignore inline JSON-LD.

Consequence: the browser-prompt rejection (`project_browser_prompt_rejected`) is **suspect** — it was
measured under the same read-counting cap; a prompt that drives more discovery/polling GETs would
exhaust the budget on discovery and score as "hurts completion." Same confound class as the earlier
"Sd rescues A=1" bug artifact.

## Design decisions (all approved)

### D1 — Caps are agent-blind observation windows

- The agent is **never told** any budget (already true — preserve; add no "N actions left" text).
- **Reads (GET) do not consume the meaningful budget.** Only mutation attempts do.
- Two generous, agent-invisible bounds satisfy Holzmann R10 (every loop terminates):
  - `MaxAttempts` — POST attempts (accepted **and** rejected). Bounds mutation thrash. Generous
    (~30) so natural play never approaches it.
  - `MaxTurns` — total loop iterations incl. GETs/unparseables. Backstop for a pure-read or garbage
    loop. Generous (~80).
- Hitting either bound is **rare by construction** and recorded as `window-truncated` — a distinct
  observation, **not** scored as a play failure. Distinct from `stalled` (agent stopped emitting
  valid actions) and from a terminal outcome.

### D2 — So surfaces its ontology through the channel agents use

- Advertise the domain-knowledge article via a **Link header** (So-gated `rel`, e.g.
  `rel="subjectOf"`) on the game resource + the existing So-gated `/strategy` route. This is So's
  **own** channel — independent of Sd's `rel="profile"` + `/profile` — so So ⊥ Sd holds and So-alone
  (0001) still works.
- **Drop / shrink** the per-turn inline JSON-LD blob so it no longer dilutes every observation.
- When **both** Sd and So are on (1111), `/profile` additionally cross-links `/strategy` — as a real
  app's profile would list related resources. This is a bonus in the Sd∩So cell, not a dependency.

### D3 — Re-introduce the browser prompt as an arm

- Restore the browser coldstart/directed prompt variants (kept in git history) as a selectable arm.
- Re-test browser vs plain **under the new reads-free regime**, where discovery/polling GETs no
  longer burn the budget. Reopens `project_browser_prompt_rejected` — decision re-derived from data,
  not inherited.

### D4 — Re-run the ladder under the new regime; observation is the product

- All cells change budget semantics → the ladder must be re-run for comparability. Order: **35b
  first** (primary test tier), then 122b, then 9b, flash.
- Per-game observations (most already in `aggregate.sh`): completion with the
  `terminal / capped / window-truncated / stalled` split, **cost + tokens**, **invalid-interaction
  attempts** (invalidMove/outOfTurn/positionTaken), plus **`/strategy` retrieval** and
  **`/profile` + Link-header usage** rates. Anomalies (harness-unfair games) dropped + listed.

## Implementation surface

| file | change |
|------|--------|
| `Driver.fs` | split the `while` bound into `attempts < MaxAttempts && step < MaxTurns`; increment `attempts` only on POST; record `window-truncated` vs terminal vs stalled in the result. |
| `Program.fs` | args `--max-attempts` / `--max-turns` (retire single `--max-actions`, or alias). |
| `sweep.sh` | pass the two bounds (env-driven, recorded to `$D/.maxactions`-style meta); add a browser-arm switch (`PROMPT=plain|browser`). |
| `aggregate.sh` | already: anomaly classifier + cap-hit. Add: `window-truncated` label, `/strategy`-fetch + `/profile`-fetch observation columns. |
| `templates/game.fs` | So render → Link header advertisement + shrink/remove inline JSON-LD. |
| `Handlers.fs` | Link header on the game resource when So; `/profile` cross-link to `/strategy` when Sd∩So. |
| browser prompt files | restore variants + `.sha256` locks (frozen-prompt discipline). |

## Integrity / constraints

- **One language (F#)** for driver + measurement subcommands; `sweep.sh`/`aggregate.sh` are the
  existing bash glue over F# outputs — extended in-kind, no new language.
- **Frozen prompts** stay hash-locked; the restored browser prompt gets its own committed lock.
- **Factor orthogonality** preserved: A, C, Sd, So each own an independent channel; So's Link header
  does not depend on Sd's `/profile`.
- Experiment code stays under `experiments/`.

## Success criteria

1. A seated agent that fetches `/strategy`, re-reads the board, and probes headers does **not** lose
   budget for it — verified: `attempts` unchanged by GETs; only POSTs move it.
2. Re-run 35b: incompletes are **not** dominated by cap-hits (window-truncation rate drops sharply
   vs the 22/60 baseline); completion reflects natural termination.
3. So cell: `/strategy` retrieval rate is **observable and non-zero** where the model chooses to
   fetch (or, if still zero, the observation cleanly attributes it — no longer masked by clutter).
4. Browser vs plain re-tested at 35b under the new regime; the rejection is confirmed or overturned
   **from fresh data**.

## Out of scope

- Changing what the factors *are* (still A×C×Sd×So).
- Raising model tiers beyond the existing Qwen ladder + haiku reference.
- Any change to the `quality` (minimax play-grade) axis.
