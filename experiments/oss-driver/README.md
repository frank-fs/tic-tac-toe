# oss-driver — how to run a reproducible sweep

Single entry point for the discovery-surface experiment (A×C×Sd×So factorial × model ladder).
**Use only the committed scripts below. Never copy a harness into a scratchpad or hand-roll a run
loop** — divergent forks (retired flags, old prompts, per-session `sweep2.sh`) are what confounded
the 2026-07-03 pilot (see `FINDINGS.md` → "Rungs 3 & 4"). If a run didn't come from these scripts on
this HEAD, treat its numbers as superseded.

## Pipeline

```
arena.sh   fresh server + proxy, one game, prints GAME_ID/URL   (up/down/status <proto|surface>)
preflight.sh   PROVE server guards are live — run before a sweep
sweep.sh   CELLS x RUNS of 3-seat cold-start games, one server/game, raw q-/s-/c-/t- artifacts
ladder.sh  descend the Qwen rungs, calling sweep.sh once per rung (no forks)
aggregate.sh   roll a sweep dir up -> clean.jsonl + completion% per cell
```

Typical run:

```bash
bash experiments/oss-driver/preflight.sh                                    # guards PASS
MODEL=qwen/qwen3.5-122b-a10b SWEEP_OUT=/tmp/qwen-122b \
  bash experiments/oss-driver/sweep.sh                                      # one rung, full cells
bash experiments/oss-driver/aggregate.sh /tmp/qwen-122b

# or the whole ladder, floor cells only, n=1:
PROMPT=plain CELLS="0000 1000" RUNS=1 SWEEP_OUT=/tmp/ttt-ladder \
  bash experiments/oss-driver/ladder.sh
```

## The rules (enforced at present — verify before trusting a run)

- **Reads are free; only mutations cost.** `Driver.fs` increments the attempt budget ONLY on `POST`
  (`attempts` at :44), and the move count only on accepted `POST <400` (:43). GETs — board re-reads,
  `/profile`, `/strategy`, header probes — are unbounded. This is the fix that de-confounded
  completion; a run that spends budget on reads is invalid.
- **Bounds** (agent-invisible): `--max-attempts 30` (POST budget) · `--max-turns 80` (total-iter
  backstop, R10) · `--max-moves 12` (accepted-move cap) · `--poll-seconds 3`.
  **`--max-actions` is RETIRED** — its presence in any script is the tell of a pre-reads-free fork.
- **3 staggered seats** per game: X, O, and a 3rd that observes (server seats the first two; the
  observer cannot seat and devolves to guessing — expected).
- **One server per game**, bulletproof teardown between games; single-instance guard (`.sweep.pid`).
- **Game-lock + terminal-guard:** a terminal arena renders no controls and `POST /delete` /
  `/restart` return 409 (stops post-game replay contamination). `preflight.sh` asserts this.
- **Prompts are hash-locked and agent-blind.** `*.md` + `*.md.sha256` must match; prompts carry NO
  self-cap ("~25 requests") or silence-as-done ("server stopped → it's done") language.

## Scoring — two orthogonal axes (do not conflate)

- `code` — conduct: FOLLOW vs INVENT (did the seat use the offered form or fabricate a move?).
- `quality` — per-player play grade from the server event log via minimax: `clean` / `missedBlock` /
  `missedWin`. Outcome (win/draw) is descriptive, NOT a success measure — a clean win is fine; a
  loss is a failure only if the loser had an attributable missed block.

## Docs split

- **`README.md`** (this file) — how to run.
- **`MODELS.md`** — the model ladder (rung slugs, pricing, MoE-vs-dense probe).
- **`FINDINGS.md`** — results + apparatus state, incl. superseded runs and the open ladder-floor gap.
- **`specs/2026-07-03-reads-free-observation-{design,plan}.md`** — the reads-free re-baseline.

## Provenance

Superseded raw runs are archived (gitignored, local) under
`experiments/results/archive/` — e.g. `ladder-pilot-2026-07-03/` (the confounded pilot + its
scratch scripts). Kept for audit; never cited as results.
