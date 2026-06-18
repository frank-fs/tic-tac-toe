# Consolidate the ERPC MCP server into `experiments/` (remove unauthorized `src/TicTacToe.Mcp`)

> Continuation of #67 on branch `erpc-identity-handshake`. Execute task-by-task with adversarial spec + quality review after each. PAUSED for user review before execution.

**Goal:** One ERPC MCP server, living only under `experiments/mcp-rpc`, carrying option-A per-request `_meta` identity. Merge the legitimate surface that drifted into the unauthorized `src/TicTacToe.Mcp`, then delete `src/TicTacToe.Mcp` so `src/` holds only the Datastar/Web app.

## Background / why
- The experiment's MCP server must live only under `experiments/`. `experiments/mcp-rpc` is the legitimate arm.
- `src/TicTacToe.Mcp` was created without authorization; the "ERPC role enforcement" work (`7cb00e2`, 2026-06-15) — `list_games`, named-square board, single-game cap, completed-game tracking, `join_game`/`playerToken` (option-B identity) — landed there by mistake, and the matrices ended up launching it (`Matrices/Smoke.fs:17 dotnet run --project src/TicTacToe.Mcp/`). So it silently became the live ERPC arm.
- This session built option-A per-request `_meta` identity in `experiments/mcp-rpc` (PlayerAssignmentStore, resolveMove, `_meta` filter, stateless `authenticate`, orchestrator injection — commits through `2b31979`). That identity core is correct and kept.
- Net: merge `src`'s legitimate surface into `experiments/mcp-rpc`, keep option-A identity, drop option-B (`join_game`/`playerToken`), repoint matrices, delete `src/TicTacToe.Mcp`.

## Resolved decisions
- **Board format: named-square dict** (src's `{ "TopLeft":"X", ... }`) — it is what the experiment has actually been running; adopt it in `experiments/mcp-rpc`, replacing the current 9-element array.
- **Identity: option A only.** `authenticate()` (stateless GUID, orchestrator-driven) + orchestrator-injected `_meta.identityToken` + lazy per-game seat binding via `PlayerAssignmentStore`. Drop `join_game`, `playerToken`, and src's `PlayerRegistry`. The LLM never handles identity.
- **Seat model:** lazy first-move binding (existing `PlayerAssignmentStore`) reproduces src's "X then O, third → game_full" behavior without an explicit join.
- **Simple is OUT OF SCOPE for this branch.** `src/TicTacToe.Web.Simple` is also misplaced, but it is *retired*, not relocated, via issue **#64** (Proto's progressive-enhancement/accessibility work absorbs it as the `text/html` representation; the orchestrator `Simple` variant collapses to Proto-as-html). This branch does not touch Simple. End state after both #67 and #64 land: `src/` = `TicTacToe.Engine` + `TicTacToe.Web` only.

---

## Task 10: Merge `src` surface into `experiments/mcp-rpc` (named board, list_games, new_game cap, post-game reads)

**Files:** `experiments/mcp-rpc/Identity.fs`, `experiments/mcp-rpc/Tools.fs`, `test/TicTacToe.McpRpc.Tests/*`, `experiments/mcp-rpc/smoke_roundtrip.py`

- [ ] **Named-square board.** Replace the array `renderBoard` (Identity.fs) with a named-square JSON object renderer: `{ squareName -> "X"|"O"|"" }` for all 9 positions (port src's `renderBoard`/`stateJson`/`whoseTurnStr`/`statusStr` shape). `MoveOutcome.Moved` should carry the board as the named-dict JSON (or carry the `MoveResult` and let the Tools layer render). Keep `resolveMove`'s logic (auth → game → playable → seat → occupancy → move) intact; only the rendered board representation changes. `whoseTurnStr` for terminal states should match src ("game_over" / "won"/"draw"/status as src emits) — preserve src's exact strings for `whoseTurn`/`status` so agent-facing output is unchanged from the live arm.
- [ ] **`list_games()`** tool: return JSON array of `{ gameId, whoseTurn, status }` over `supervisor.ListActiveGames()` (port src `list_games`). No identity needed.
- [ ] **`new_game()` single-game cap:** if `supervisor.ListActiveGames()` length ≥ 1 → return `{ "error": "MaxGamesReached" }`; else create and return `{ gameId, board(named), whoseTurn, status }` (port src behavior; the experiment pre-creates exactly one game).
- [ ] **Post-game readability for `get_board`/`get_state`:** a finished game must still return its final board/status (src used a `completedGames` store). Verify whether `GameSupervisor.GetGame` retains finished game actors long enough (it answers `Won`/`Draw` until the 5-min cleanup); if it does, reuse it. If not, add a singleton completed-game store mirroring src's observable behavior. Match src's `get_board`/`get_state` output (named board, `whoseTurn="game_over"` on terminal, gameId on get_state).
- [ ] **Keep option-A identity:** `authenticate()` stateless minter, `make_move(user: ClaimsPrincipal, gameId, position)` (no playerToken), `_meta` filter unchanged. No `join_game`. Remove any array-board assumptions.
- [ ] **Tests:** update `ResolveMoveTests`/`IdentityTests` for the named-board format (assert `board["TopLeft"]="X"` instead of `board[0]`). Add tests: `list_games` returns the active game; `new_game` second call → `MaxGamesReached`; finished game still readable via `get_state`. Keep PlayerAssignmentStore + resolveMove identity coverage.
- [ ] **smoke_roundtrip.py:** update assertions to named board (`board["TopLeft"]=="X"`, `board["TopCenter"]=="O"`); keep the multi-identity-on-one-connection cases (A→X, B→O, A-still-X, unauthenticated, malformed `_meta`). Add a `list_games` case.
- [ ] Build (Release) 0 errors; `dotnet test test/TicTacToe.McpRpc.Tests/` all pass; smoke all cases pass (paste output). Commit.

## Task 11: Repoint matrices + rewrite the Rpc agent prompt

**Files:** `experiments/orchestrator/Matrices/Smoke.fs`, `experiments/orchestrator/Orchestrator.fs`, possibly `experiments/orchestrator/Types.fs`

- [ ] **Repoint** `Smoke.fs` ERPC `mcpServer` config: `Arguments = [| "run"; "--project"; "experiments/mcp-rpc"; ... |]` (launch the experiments server). Keep `Name = "tictactoe-mcp"` so `surfaceOf` still maps to `Rpc` (confirm Types.fs:33 mapping). Verify the working-directory/relative-path the orchestrator uses to launch (`dotnet run --project <path>`) resolves to `experiments/mcp-rpc` from the repo root.
- [ ] **Rewrite the `Rpc` system prompt** (Orchestrator.fs:56-60). Remove `join_game`/`playerToken` language. New flow: "Call `list_games` to find the game. Read the board with `get_state`. To move, call `make_move` with a `position`; your identity is handled automatically and a move only succeeds on your turn. `get_state` only reflects new moves when you call it again." No token instructions.
- [ ] Confirm the orchestrator's ERPC branch (already added in Task 8: per-agent `authenticate()` + `_meta` injection) is consistent with the repointed server (it is — `authenticate` exists on `experiments/mcp-rpc`). Build orchestrator; `dotnet test test/TicTacToe.Orchestrator.Tests/` (incl. the identity integration test, which already targets `experiments/mcp-rpc`). Commit.

## Task 12: Delete `src/TicTacToe.Mcp` + its tests

**Files:** remove `src/TicTacToe.Mcp/`, `test/TicTacToe.Mcp.Tests/`, sln entries

- [ ] Confirm NOTHING else references `src/TicTacToe.Mcp` / `TicTacToe.Mcp` namespace / the `tictactoe-mcp` project beyond the now-repointed matrix (grep the repo, incl. other matrices, scripts, docs).
- [ ] `git rm -r src/TicTacToe.Mcp test/TicTacToe.Mcp.Tests`. Remove both `dotnet sln` entries (`TicTacToe.Mcp`, `TicTacToe.Mcp.Tests`).
- [ ] Confirm `src/` now contains no MCP server. After this branch `src/` = `Engine` + `Web` + `Web.Simple` (Simple retired separately via #64 → eventual `src/` = `Engine` + `Web`). `dotnet build TicTacToe.sln` 0 errors. Commit.

## Task 13: Close-out — multi-agent E2E + whole-branch review + self-reflect

- [ ] Full solution build (0 errors). Run Engine, McpRpc, Orchestrator test suites — paste counts.
- [ ] Multi-identity E2E via the orchestrator's own `McpClientSet` against the repointed `experiments/mcp-rpc` (the existing Orchestrator integration test, with named board): two tokens → two seats on one connection.
- [ ] Whole-branch adversarial review (`f0e88e9..HEAD`): one ERPC server under `experiments/` only; option-A `_meta` identity (no LLM-facing token); named board parity with the prior live arm; `list_games`/cap/post-game reads present; matrices launch `experiments/mcp-rpc`; `src/TicTacToe.Mcp` gone; `src/` is Datastar/Web only; solution builds; all suites green.
- [ ] Self-reflect vs both theses (identity-model parity unconfounded; agent-ability/fair instrument). Then finishing-a-development-branch.

## Notes / risks
- Board-format change touches `make_move`/`get_board`/`get_state` output + their tests + the smoke; the identity core (PlayerAssignmentStore, resolveMove control flow, `_meta` filter, authenticate, orchestrator injection) is unaffected.
- Deletion of `src/TicTacToe.Mcp` is the irreversible step — Task 12 gates it on a full reference grep.
- The existing captured `smoke-erpc-*` result data assumed the option-B (`join_game`/`playerToken`) flow; with option A those would be re-run. Out of scope here (data, not code) — flag for the experiment log.
