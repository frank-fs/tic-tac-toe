# FINDINGS — moved to `agent-hypothesis`

**This file is a pointer. The results record no longer lives here.**

The tic-tac-toe experiment is **specimen #1** of a cross-specimen hypothesis, so its results,
conclusions and raw evidence were migrated to the hypothesis repo, where they can be compared against
other specimens (spec 003c, issue frank-fs/agent-hypothesis#10).

## New home

| What | Where (repo `frank-fs/agent-hypothesis`) |
|---|---|
| The record — results, corrections, capstone | `results/tic-tac-toe/FINDINGS.md` |
| Live raw runs (10 dirs) | `results/tic-tac-toe/archive/` |
| Retracted / confounded runs — **DO NOT CITE** | `results/tic-tac-toe/superseded/` |
| Thesis-level conclusions | `docs/design/thesis-evolvable-dual-audience-contract.md` §9 |

The **git history came with it**: `git log --follow results/tic-tac-toe/FINDINGS.md` in
agent-hypothesis shows all 27 original commits, including the correction/retraction trail. It was
grafted with `git-filter-repo` from a throwaway clone — **tic-tac-toe's own history was not
rewritten**, so every commit referenced from the migrated record still resolves here.

## What stays here

This repo remains the **apparatus** for specimen #1 — the surface under test (`src/TicTacToe.Web*`),
the arms, the driver (`experiments/oss-driver/`), the hash-locked prompts and their `.sha256` locks,
and the per-cell ground truth. Only the *results* moved.

`experiments/results/archive/` (gitignored, ~69M) may still exist in your working copy. It is now
committed in agent-hypothesis and is safe to delete locally.
