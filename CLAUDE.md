# tic-tac-toe: Agent-Hypothesis Experiment

F# .NET 10.0 web framework demonstrating HATEOAS discovery and semantic layers (F0–F8) across two server variants (V_proto / V_swagger) measured against an RPC MCP null hypothesis.

## Commands

```bash
# Build
dotnet build

# Run unit tests
dotnet test test/TicTacToe.Engine.Tests/

# Run server (default port 5228)
dotnet run --project src/TicTacToe.Web/

# Run integration tests (requires server on 5228)
dotnet test test/TicTacToe.Web.Tests/
```

## H-Wave Workflow

Before claiming work complete on any H-issue:

1. `dotnet build`
2. `dotnet test test/TicTacToe.Engine.Tests/`
3. `dotnet test test/TicTacToe.Web.Tests/` (if applicable)
4. Verify server runs and basic routes respond
5. Use `/verification-before-completion` for larger changes

### Git Workflow

**Worktree only.** Edits on master are blocked.

```bash
git worktree add .claude/worktrees/<name> -b <branch-name>
cd .claude/worktrees/<name>
# work, test, commit
git merge --ff-only <branch>  # (in main worktree)
git push origin master        # Pre-Push approval required
```

**Pre-Push approval:** Respond to the hook prompt, or bypass with `TIC_TAC_TOE_ALLOW_PUSH=1 git push origin master`.

## Phases

| Phase | Issues | Status |
|-------|--------|--------|
| **Harness 0** | H0 | Todo |
| **Harness 1** | H1, H2, H4, H6 | Todo |
| **Gate** | G0 | Todo |
| **F0 Baseline** | F0 | Todo |
| **F1-F4 Discovery** | F1-F4 | Todo |
| **F6-F8 Semantic** | F6-F8 | Todo |
| **Analysis** | H3 | Todo |

**Project board:** `https://github.com/orgs/frank-fs/projects/2`

## Code Discipline

- **Type safety.** Preconditions at boundaries, leverage F# inference.
- **Idiomatic F#.** CEs, DUs, Option, pipelines.
- **No silent failures.** Log via `ILogger`; never swallow exceptions.
- **Measurement precision.** HTTP transcripts, RPVA, invalid-request rate per issue spec.
