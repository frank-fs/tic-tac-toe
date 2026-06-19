# Proto Phase B: per-resource SSE streams + per-game broadcast filtering

**Date:** 2026-06-18
**Status:** spec — ready for implementation plan
**Issue:** #62 ([PE-3] Per-resource streams + SseBroadcast per-game filtering — Phase B)
**Branch:** `proto-phase-b`
**Supersedes:** the Phase B section (Issues 3 & 4) of `2026-06-15-proto-same-url-conneg-pe-design.md`

## Why this revises the earlier spec

The 2026-06-15 spec's Phase B assumed **same-URL content negotiation** for the streams
(serve `text/html` or `text/event-stream` off the same handle by `Accept`) and **retiring the
global `/sse`**. Two facts changed that:

1. **`Frank.Datastar`'s SSE has no native content negotiation.** Same-URL streaming would need
   a heavy workaround for no measurable gain. Decision: **keep separate stream endpoints.**
2. The dashboard stream is a **kept** part of the design (it gives the JS client live updates
   over the server-rendered base); it is **not** retired.

Datastar disambiguation is by the **`datastar-request` header**, not `Accept` (already used for
the move POST, `Handlers.wantsHtmlResponse`) — but with separate endpoints we do not rely on it
for the streams. No-JS clients hit the HTML resources; JS opens the stream endpoints.

## Experiment context (unchanged)

Three arms over one shared engine (rule-consistency control):
- **Simple** — stable baseline: common request/response HTML (low fidelity; does not model turn
  ownership).
- **ERPC** — stable baseline: common JSON-RPC.
- **Proto** — the enriched hypothesis: progressive enhancement supporting request/response HTML
  **and** Datastar streaming at **equal fidelity**, with proper affordances driving agent
  interaction across both. This issue advances Proto's streaming half.

## Current state (confirmed in code, 2026-06-18)

- `GET /` (`Handlers.home`, Handlers.fs:154) server-renders **all** active games' boards (Phase A
  landed). No-JS discoverable + playable; refresh re-fetches.
- `resource "/sse"` (Program.fs:99) → `Handlers.sse` (Handlers.fs:183): a single **global** stream.
  On connect it **clears** `#games-container` (Handlers.fs:192) then **appends** every game's board,
  then forwards broadcast events.
- `SseBroadcast` (SseBroadcast.fs): `subscribe userId` keys a channel by `Guid` with `(userId,
  channel)` — **no game filter**. `broadcast` / `broadcastPerRole` / `sendToUser` fan out to all.
- `subscribeToGame` (Handlers.fs:59) renders per-role on each `MoveResult` and calls
  `broadcastPerRole` → every subscriber gets a `PatchElements` (full board, `id="game-{gameId}"`).
- **Therefore moves on existing games already morph in place** (Datastar default morph matches the
  board `id`). New games created after a client connects do **not** appear until reconnect.

## Design (approved)

### Part 1 — Dashboard `/sse` (primary goal: play from the home page)

**1a. Drop the connect-time container clear.** Remove the `#games-container` clear (Handlers.fs:192).
The home page already server-rendered every board; clearing+re-appending destroys and rebuilds them
(flash, screen-reader focus churn, the render-then-resync the earlier spec called out). Instead, on
connect send each current game's board as a **morph-by-id** patch (`PatchElements`), which resyncs the
already-rendered board to live state and captures any move made in the render→connect gap.

**1b. Append new games live.** When `Handlers.createGame` creates a game, broadcast a
**`PatchElementsAppend`** of its board into `#games-container` **to dashboard (`None`-filter)
subscribers only** so already-connected dashboards gain it without a reconnect. (A per-game
subscriber must not receive another game's append; before Part 2's filtering lands all subscribers
are dashboard, so this is a no-op distinction until 2a.)

**1c. Existing-game moves** — unchanged; already morph by id via `broadcastPerRole`.

### Part 2 — Per-game stream (secondary: parity per game)

**2a. `SseBroadcast` per-game filtering.** Extend a subscriber's registration with an optional
**game filter**: `None` = dashboard (receives all games' events); `Some gameId` = receives only that
game's events. A game's broadcast (move morph) routes to *(dashboard subscribers) ∪ (subscribers filtered to that
game)*. A new-game append routes to *dashboard subscribers only*. `broadcastPerRole` becomes the
per-game-aware path (takes the target `gameId`).

**2b. `/games/{id}/sse` resource** (`requireAuth`). Handler subscribes with `Some id`; on connect
sends that game's board as a **morph-by-id** snapshot (no clear); then forwards only that game's
events. Idempotent re: the existing `subscribeToGame` (assert no double-subscribe).

**2c. Per-game page wiring.** The `getGame` HTML opens `@get('/games/{id}/sse')` on load (Datastar)
so the individual game page updates live, mirroring the dashboard.

### Data flow (after this issue)

1. `GET /` (no-JS) → full dashboard. JS opens `@get('/sse')` → morph-by-id resync of each board +
   live deltas; new games arrive as appends.
2. `GET /games/{id}` (no-JS) → full board. JS opens `@get('/games/{id}/sse')` → that board's
   resync + its deltas only.
3. `POST /games/{id}` (move) → 202 → board change → `broadcastPerRole` → that game's dashboard
   *and* per-game subscribers morph the board by id.
4. No-JS: refresh re-GETs current state (the freshness path).

## SseBroadcast shape

```
subscribe : userId:string -> filter:string option -> Channel<SseEvent> * IDisposable
   // filter = None  -> dashboard (all games)
   // filter = Some gameId -> only that game's events
```

Broadcast for a game targets subscribers whose filter is `None` or `Some thatGameId`. `broadcast`
(unfiltered, all) and `sendToUser` retained as-is for any global use; the per-game-aware
`broadcastPerRole` gains the target `gameId` so it can filter. Keep `broadcastPerRole`'s per-user
board personalization intact.

## Acceptance criteria

- **AC1 (filtering):** a `Some gameId` subscriber receives only that game's events; a `None`
  subscriber receives all games' events (SseBroadcast unit/integration test).
- **AC2 (dashboard append):** a game created while a dashboard stream is open is appended to
  `#games-container` on that open connection (no reconnect) — E2E.
- **AC3 (per-game connect-resync):** opening `/games/{id}/sse` after a move yields that board's
  current state as the first payload — E2E.
- **AC4 (morph, no clear):** a move patches the existing board element in place; the connect path
  no longer clears `#games-container` (assert single board element per game, no clear) — E2E.
- **AC5 (no-JS unbroken):** `GET /` and `GET /games/{id}` with no JS still return the full
  server-rendered state; refresh shows an opponent's move (regression — Phase A tests stay green).

## Out of scope

- Retiring the global `/sse` (kept).
- Same-URL content negotiation for streams (Frank.Datastar limitation).
- Last-Event-ID / versioned replay (connect-resync is sufficient).
- Engine / CQRS 202+SSE command path / `broadcastPerRole` per-user semantics changes.
- Any orchestrator / experiment-arm changes.

## Known tradeoff

Dropping the connect clear means a game created in the sub-second window **between** the home
render and the dashboard stream connect could be absent until its first move or a refresh (the
no-JS freshness path covers it). Acceptable under the "connect-resync, no Last-Event-ID" scope;
documented rather than engineered around.

## Implementation note (2026-06-19): config-driven E2E

During implementation the experiment config surfaced a refinement to the test approach. The
arms (ERPC, Simple, Proto) all run with `InitialGames=1, MaxGames=1` (server starts with one
game, rejects further creates), so the dev multi-game "append a new game" path is never
exercised in a run. A cross-client append test against an externally-started server was also
timing-flaky (the live append has no replay; a game created before the watcher's `/sse`
connected was lost).

Resolution: a **configurable-server E2E harness** (`ConfiguredServer`) launches the built web
app on a free port with chosen `TICTACTOE_INITIAL_GAMES` / `TICTACTOE_MAX_GAMES`, waits for
readiness, and disposes after — so each fixture pins its own config and owns clean state. Two
fixtures result:
- **`ExperimentConfigStreamTests` (1/1)** — the single game's move **morphs live in place**
  (board stays one element; AC4), a second create is **rejected** (`409 MaxGamesReached`), and
  the per-game stream **connect-resync** carries the current board (AC3). This is the run
  condition all three arms share.
- **`MultiGameConfigStreamTests` (6/unlimited)** — a new game is **appended live** to the open
  dashboard as a single board (AC2 + AC4) via the in-page create affordance (deterministic
  because the click goes through connected Datastar).

AC1 (filtering) stays a pure unit test; AC5 (no-JS) is the unchanged Phase A suite. The
cross-client append behavior was additionally verified by hand (curl stream + a real browser:
exactly one board, no duplicate). All new tests are deterministic (ran clean repeatedly); the
full Web.Tests suite is green (118/118).

## Risks

- **Double-subscribe:** `getGame` and the per-game stream both touch `subscribeToGame` — assert
  idempotency (the existing `gameSubscriptions` guard, Handlers.fs:60).
- **Markup parity:** dashboard and per-game render both go through `renderGameBoard` — no drift.
- **Append vs morph selector:** new-game append targets `#games-container`; board morph targets the
  board `id`. Verify a newly-appended board then morphs in place on its first move (no duplicate).
