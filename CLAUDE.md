# tic-tac-toe

F# .NET 10.0 — HATEOAS discovery layers (F0–F8) vs. RPC MCP null hypothesis.

## Build & Test

```bash
dotnet build
dotnet test test/TicTacToe.Engine.Tests/
dotnet test test/TicTacToe.Web.Tests/
```

See `test/CLAUDE.md` for integration test setup, and `experiments/test/CLAUDE.md` for comparison-arm (Simple) tests and the haiku-subagent harness.

## Workflow

**Worktree only.** Direct edits to master are blocked.

```bash
git worktree add .claude/worktrees/<name> -b <branch>
git merge --ff-only <branch>   # back in main worktree
```

Push: `TIC_TAC_TOE_ALLOW_PUSH=1 git push origin master`

### Before Claiming Complete

1. `dotnet build`
2. `dotnet test test/TicTacToe.Engine.Tests/`
3. `dotnet test test/TicTacToe.Web.Tests/` (if web changes)
4. Server responds on routes
5. `/verification-before-completion` for large changes

## Phases

| Phase | Issues |
|-------|--------|
| Harness 0 | H0 |
| Harness 1 | H1, H2, H4, H6 |
| Gate | G0 |
| F0 Baseline | F0 |
| F1–F4 Discovery | F1–F4 |
| F6–F8 Semantic | F6–F8 |
| Analysis | H3 |

[Board](https://github.com/orgs/frank-fs/projects/2) — all Todo

## Code Discipline

**F#:** Type safety at boundaries. Idiomatic CEs/DUs/Option/pipelines. No silent failures — `ILogger` always.

**Holzmann (Power of 10):**
- Nesting ≤ 2 levels (R9)
- Bound every loop/recursion (R10)
- One function, one job, ≤ 60 lines (R11)
- Preconditions at all boundaries (R12)
- No module-level mutable state (R13)
- No I/O behind pure-looking names (R14)
- Max one indirection layer (R15)

**Karpathy:**
- State assumptions; surface tradeoffs before coding
- Minimum code — no speculative abstractions
- Surgical changes — touch only what the task requires
- Define verifiable success criteria first
