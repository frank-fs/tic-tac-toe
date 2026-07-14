# `experiment/` — the harness adapter (sample side)

This repo is **specimen #1** of the agent-hypothesis. Per ADR 001 (three-repo topology), it ships
the *app under test* plus the thin adapter the harness needs — nothing else. The harness itself and
the results record live in **`frank-fs/agent-hypothesis`**.

| Path | What |
|---|---|
| `manifest.json` | harness↔sample contract (spec 001 §2): launch, cell toggle, seats, artifact paths |
| `groundtruth/cell-*.json` | per-cell ground truth for the 16-cell A/C/Sd/So factorial |
| `directed-facts.json` | the facts the directed (non-cold-start) prompt is allowed to state |
| `scorer/` | `TicTacToe.Quality` — the sample's `IQualityScorer` plugin (minimax blunder analysis) |
| `erpc/` | `TicTacToe.McpRpc` — the **RPC/MCP null-hypothesis server** (the ERPC arm) |

## Results — moved to `agent-hypothesis`

**The results record no longer lives in this repo** (spec 003c, `frank-fs/agent-hypothesis#10`).
Because tic-tac-toe is one specimen of a cross-specimen hypothesis, its results, conclusions and raw
evidence were migrated so they can be compared against other specimens.

| What | Where (repo `frank-fs/agent-hypothesis`) |
|---|---|
| The record — results, corrections, capstone | `results/tic-tac-toe/FINDINGS.md` |
| Live raw runs | `results/tic-tac-toe/archive/` |
| Retracted / confounded runs — **DO NOT CITE** | `results/tic-tac-toe/superseded/` |
| Pre-oss-driver smoke + proto-A/B runs | `results/tic-tac-toe/early-runs/` |
| Thesis-level conclusions | `docs/design/thesis-evolvable-dual-audience-contract.md` §9 |
| `HYPOTHESIS.md`, `L3_FINDINGS.md` | `docs/` |

The **git history came with it**: `git log --follow results/tic-tac-toe/FINDINGS.md` in
agent-hypothesis shows all 27 original commits, including the correction/retraction trail.
tic-tac-toe's own history was **not** rewritten, so every commit referenced from the migrated record
still resolves here.

## The driver also moved

The 16-cell sweep driver (`experiments/oss-driver/`) was extracted to agent-hypothesis
`src/Harness/` (issue #8) and the duplicate deleted here (spec 003b) — one harness, no drift. The
hash-locked cold-start prompt moved with it, byte-identical
(`35e2bd7961ae140f1c5b4424cd349d6812041e3ffe800cb9e3448db5fabd2d78`, the same `coldstart_sha` every
archived run records). Run sweeps from agent-hypothesis, pointing the harness at this repo's
`experiment/manifest.json`.
