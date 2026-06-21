# Haiku subagents as players (capability ceiling)

Play the tic-tac-toe apps with **Claude haiku subagents** as the X and O players, over
plain `curl`. This removes the local-inference throughput cage (remote, capable models)
and establishes what's achievable when hardware isn't the constraint.

**Validated 2026-06-21:** Proto → draw (9 moves); Simple → X-win (5); minimal-prompt →
X-win (5). All completed, **0 rejected moves**. (gemma+M2 local: 0 completions.)

## Run it

1. **Start a single-game, no-JS server with an event log.**

   Proto (port 5228):
   ```bash
   TICTACTOE_INITIAL_GAMES=1 TICTACTOE_MAX_GAMES=1 TICTACTOE_DISABLE_JS=1 \
     TICTACTOE_REQUEST_LOG_PATH=/tmp/proto-game.jsonl \
     dotnet run --project src/TicTacToe.Web/ --urls=http://localhost:5228
   ```
   Simple (port 5328 — has a JSON API, no JS to disable):
   ```bash
   TICTACTOE_INITIAL_GAMES=1 TICTACTOE_MAX_GAMES=1 \
     TICTACTOE_REQUEST_LOG_PATH=/tmp/simple-game.jsonl \
     dotnet run --project experiments/src/TicTacToe.Web.Simple/ --urls=http://localhost:5328
   ```

2. **Discover the game id** from `/` (`games/<guid>` for Proto, `arenas/<guid>` for Simple).

3. **Spawn two `haiku` subagents** (Agent tool, `general-purpose`, background), one X one O.
   Each gets its own cookie jar (`curl -c jar /login` once, then `-b jar`), the read/move
   contract, and "play to win, drive to completion." A **minimal** prompt is enough — do
   not over-specify the loop; capable models infer poll/wait/retry on their own.

4. **Watch the event log** for `"event_type":"game_over"`; tally accepted vs rejected.

## Contract cheatsheet

| | Proto (5228) | Simple (5328) |
|---|---|---|
| Login | `GET /login` → cookie `TicTacToe.User` | `GET /login` → cookie `TicTacToe.SimpleUser` |
| Game route | `/games/{id}` (board inline on `/`) | `/arenas/{id}` (own page) |
| Read state | HTML, token `data-game-status` | `GET` + `Accept: application/json` → `{board[9],status,whoseTurn}` |
| Move | `POST /games/{id}` `-d player=X&position=Name` | `POST /arenas/{id}` `-d player=X&position=Name` |
| Reset | restart the server | `POST /arenas/{id}/restart` |

Positions: `TopLeft TopCenter TopRight MiddleLeft MiddleCenter MiddleRight BottomLeft
BottomCenter BottomRight`. No CSRF enforced on the move POST (cookie-only).

## Gotchas

- Tell players: **never delete/reset**; a 404 *after* the game started means it ended —
  stop, don't report a crash; report only server-confirmed state. (A confused agent
  hallucinated a "backend failure" from a benign post-game 404; the event log is truth.)
- Don't truncate the live event log — the server's open writer desyncs into a sparse
  file. Use a fresh log path per run.
- Two cookie jars = two distinct users (login with no cookie = new identity).

## Not yet supported: ERPC

The mcp-rpc (ERPC) surface derives identity from a per-request `_meta.identityToken` that
only the bespoke orchestrator injects — a generic MCP client can't set per-subagent
`_meta`, so two subagents collapse to one identity. Fix: add an explicit `identityToken`
tool parameter to `make_move`/`get_state`.
