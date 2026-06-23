# Haiku subagents as players (capability ceiling)

Play the tic-tac-toe arms with **Claude haiku subagents** as the X and O players. Removes
the local-inference throughput cage (remote, capable models) → establishes what's
achievable when hardware isn't the constraint. Three arms: **Proto** (no-JS HTML, curl),
**Simple** (HTML + JSON API, curl), **ERPC** (purpose-built MCP tools).

**Validated 2026-06-22:** all three arms complete from the minimal floor — Proto draw,
Simple x_wins, ERPC draw, ~0 game-rule rejections. (gemma+M2 local: 0 completions even
with the full contract → haiku ≫ gemma-4-e4b.) The verbose contract was ~80% cuttable; the
irreducible floor is the stateful protocol (identity-persist · turn · occupancy · pacing).

## Run it

1. **Bring up a curl arm** (server + HTTP-logging proxy, fresh game, clean teardown):
   ```bash
   ./arena.sh up proto      # → GAME_PATH / URL (proxy 6228) / LOG / HTTPLOG / PID
   ./arena.sh up simple     # proxy 6328
   ./arena.sh down proto    # kills server + proxy by pidfile AND port (SIGKILL fallback)
   ./arena.sh status proto
   ```
   Agents play through the **proxy** URL (`http://localhost:PORT+1000`); it forwards to the
   real server and logs every request's `{method,path,status}` to `/tmp/arena-<arm>.http.jsonl`.

2. **ERPC** is the stdio MCP server `tictactoe-rpc` (Claude's own config, not shell-managed).
   Set up its game via the MCP tools: `list_games` → `new_game` → pass the gameId to the agents.

3. **Spawn two `haiku` subagents** (Agent tool, `general-purpose`, background), one X one O,
   using the floor prompts in `prompts.md`. Curl agents get the proxy URL + a per-role cookie
   jar; ERPC agents authenticate() for a per-seat token.

4. **Watch** the game event log for `game_over`; **analyse friction** with `friction.py`:
   ```bash
   uv run --no-project friction.py proxy /tmp/arena-proto.http.jsonl
   uv run --no-project friction.py erpc  /tmp/erpc-game.jsonl <gameId>
   ```
   Splits requests into reads(poll) / writes-accepted / writes-rejected / auth-redirects —
   so polling is never conflated with rejection.

## Contract cheatsheet

| | Proto (5228, proxy 6228) | Simple (5328, proxy 6328) | ERPC (MCP) |
|---|---|---|---|
| Identity | cookie jar (`-c` once, `-b` after) | cookie jar | `authenticate()` → token |
| Read | HTML, `data-game-status` | `Accept: application/json` → `{board[9],status,whoseTurn}` | `get_state(gameId)` |
| Move | `POST /games/{id}` `-d player=X&position=Name` | `POST /arenas/{id}` same | `make_move(gameId,position,identityToken)` |

Positions: `TopLeft TopCenter TopRight MiddleLeft MiddleCenter MiddleRight BottomLeft
BottomCenter BottomRight`. No CSRF on the move POST (cookie-only).

## The minimal floor (see `prompts.md`)

The bare "play tic-tac-toe at \<URL\>" prompt FAILS (agents can't infer the stateful
protocol → `NotAPlayer` storm). The irreducible floor a capable model cannot infer:
- **Identity-persist** — log in/auth ONCE, reuse the SAME cookie jar/token on every call.
- **Turn** — only act on your turn; re-read between turns.
- **Occupancy** (curl) — only play an empty square.
- **Pacing** — it's not a race; wait ~3-5s between checks, cap checks; a quiet/stopped
  server means the game ended, not a crash.

The move format, position names, and status tokens are all discoverable → omitted.
Mastery personas (Novice/Skilled/Expert) layer on top of the floor.

## Gotchas

- **Never delete/reset/create** (tell players); a 404 / `game_not_found` AFTER the game
  started means it ended — stop, report server-confirmed state, don't call it a crash.
- **Let both agents come to rest before teardown** — killing a server while a player is
  still polling makes it misreport "connection refused / crash" (cosmetic; the log is truth).
- **Single-game (`MAX_GAMES=1`)** — any probe move contaminates the one board; `arena.sh up`
  always restarts fresh (a NEW random game-id each time = proof the process is fresh; a
  repeated id = a zombie reattach, which `stop_server`'s port+SIGKILL now prevents).
- **ERPC server is `--no-build`** — code changes (e.g. the `state_read` read-logging in
  `Tools.fs`) only take effect after the MCP server PROCESS is relaunched, which happens
  when the Claude Code CLI itself restarts (not `/clear`, not conversation resume).

## Friction measurement

- Curl reads/writes/rejects: from the proxy log (method+status), via `friction.py proxy`.
- ERPC reads: `get_state`/`get_board` log `state_read` events (added 2026-06-22); writes &
  rejections already logged. `friction.py erpc`.
- The earlier "ERPC ~1:1" was an artifact — ERPC reads were unlogged. Measure, don't infer.
