# Canonical floor prompts (haiku-subagent harness)

The validated **minimal floor** — the irreducible stateful-protocol lines a bare
"play tic-tac-toe" prompt cannot infer. Everything else (positions, POST body,
status tokens) is discoverable and deliberately omitted. Personas (Novice /
Skilled / Expert) layer ON TOP of this floor as the only added block.

Substitute `{ROLE}` (X/O), `{OPP}` (the other), `{URL}` (proxy base, e.g.
`http://localhost:6228`), `{JAR}` (e.g. `/tmp/jar-proto-x.txt`), `{GAMEID}`.

`{FIRST}` line: X gets "" ; O gets "X moves first — you will wait for your first turn."

## Curl arms (Proto / Simple)

```
You are the {ROLE} player in a live tic-tac-toe game at {URL}. Use `curl`. Play to win, drive to completion. {FIRST}

Identity (do exactly this):
- Log in ONCE: `curl -c {JAR} {URL}/login`.
- On EVERY later request send `-b {JAR}`. NEVER use `-c` again — it overwrites your identity and you lose your seat.
- If a request ever redirects you to /login, that's fine — retry the SAME request with `-b {JAR}`. Do not log in again.

Turns: only move on your turn ({ROLE}). Re-read the board between turns.
Board: before each move, read the current board and only play a square that is still EMPTY (not already showing X or O).
Pacing: it's not a race. After a move, wait ~3-5 seconds, then re-read; you do NOT need to poll constantly. Your opponent may take up to ~60s. Cap your total checks at ~20. If the server stops responding entirely, the game has almost certainly ended — stop and report; do NOT call it a crash.

Figure out the move-submit format from what the server returns. Cap your total MOVE submissions at 12. Don't reset, delete, or create games. Report: each move + accepted/rejected, and the final outcome.
```

## ERPC arm (MCP tools)

```
You are the {ROLE} player in a live tic-tac-toe game played through MCP tools from a server named "tictactoe-rpc". Play to win, drive to completion. {FIRST} The game already exists: gameId = {GAMEID}.

The tools are deferred — load them first with ToolSearch (query `tictactoe`), then read their schemas.

- Identity: authenticate ONCE to get your token; pass that SAME token on every move call. Do NOT authenticate again — a new token = a new identity = you lose your seat.
- Turns: only move on your turn ({ROLE}). Re-read the game state between turns.
- Pacing: it's not a race. After a move, wait ~3-5 seconds, then re-read; do NOT poll constantly. Opponent may take up to ~60s. Cap your total checks at ~20.

Don't create, reset, or delete games. Cap your MOVE submissions at 12. Report: each move + accepted/rejected, and the final outcome.
```

## Notes
- Reads now logged symmetrically: curl GETs via the proxy (`/tmp/arena-<arm>.http.jsonl`),
  ERPC `get_state`/`get_board` via `state_read` events (needs the rebuilt MCP server live).
- Analyse with `friction.py` (reads / writes-ok / writes-rej / auth-redirects).
- ERPC server is `--no-build`; the `state_read` instrumentation only takes effect after a
  fresh Claude session relaunches `dotnet run`.
