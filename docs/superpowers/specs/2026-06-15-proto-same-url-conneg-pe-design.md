# Proto: same-URL content negotiation + full progressive enhancement

**Date:** 2026-06-15
**Status:** spec — ready for `/milestone-execute` in a later session
**Branch to implement on:** a fresh worktree off `main`

## Thesis

Make Proto a faithfully progressively-enhanced app where **each resource is one durable
URL with two representations**, content-negotiated by `Accept`:

- `Accept: text/html` → the full current state, server-rendered, usable with **no JS**
  (forms POST moves; **refresh re-fetches current state**).
- `Accept: text/event-stream` → the same resource as a live stream (connect-resync
  snapshot, then live deltas) for JS clients.

This fixes the discovery failure found in the L3 study (today `GET /` is an empty shell;
the games appear only via a separate global `/sse`, so a no-JS/agent GET sees no games and
no link trail) and gives the browser the **re-fetch-by-handle freshness** that RPC agents
sustain (`see experiments/L3_FINDINGS.md`). One handle, two representations, is the
linked-data shape the F0–F8 study needs.

**Done looks like:** a no-JS GET of `/` returns the full game list with playable forms; a
move by one player is visible to another after a refresh; JS clients get the same state
live via an SSE connection to the *same URL*; screen readers hear updates via `aria-live`.

## Current design (confirmed in code, 2026-06-15)

- `GET /` (`Handlers.home`, Handlers.fs:125): renders a **shell** — `homePage` with an
  empty `#games-container`. Games are injected only by the SSE handler on connect
  (`Handlers.sse` clears the container at Handlers.fs:148, then appends each board via
  `renderGameBoard`, 151-159). **This is the discovery gap.**
- `GET /games/{id}` (`getGame`, Handlers.fs:220): **already server-renders** the full board
  (Handlers.fs:247). Keep this; extend it with the event-stream representation.
- `resource "/sse"` (Program.fs:101-105): a single **global** stream feeding the dashboard.
  To be retired in favor of per-resource streams.
- `SseBroadcast` (SseBroadcast.fs): global `broadcast` + `broadcastPerRole`; subscribers are
  not filtered by game.
- Move flow: `POST /games/{id}` → 202 → board change → broadcast → SSE patch.

## Design (approved)

### Approach
Content-negotiate **at the handler level**: each resource's `get` inspects `Accept` and
branches — `text/html` → server-rendered state; `text/event-stream` → that resource's live
stream. Chosen over a negotiation middleware/combinator: the branch is localized,
unit-testable per resource, and avoids new Frank machinery. Retire `resource "/sse"`.

### Resources
- **`GET /`** — html (default): render layout + **all active games** server-side, reusing
  `renderGameBoard` per game (the enumeration that already exists in the SSE handler at
  151-159, moved into the page render). event-stream: **dashboard stream** (all games).
- **`GET /games/{id}`** — html: keep current server render. event-stream: **that game's
  stream only.**

### Streams (per resource)
- On (re)connect: read **live** state and send a **full snapshot** (per game), then forward
  live deltas.
- **Full-board events, morph-by-id:** drop the container *clear* (today's Handlers.fs:148);
  send each board as a normal id'd patch so Datastar morphs it **in place**. This removes
  the render-then-resync flash, the duplicate-append risk, and screen-reader focus churn,
  while keeping the connect-resync race-safety.
- Race handling = **connect-resync snapshot** (no Last-Event-ID this milestone). The snapshot
  reads live state at connect, so any move between the html render and the stream connect is
  captured; full-board events mean any later event also resyncs. (Deterministic
  Last-Event-ID replay is a noted future optimization, out of scope.)

### SseBroadcast changes
Add **per-game filtering** on the subscriber side: a per-game stream subscribes to one
`gameId`; the dashboard stream subscribes to all. Keep `broadcastPerRole` (per-user board
rendering) intact.

### Data flow
1. `GET /` html → full dashboard (works no-JS). Datastar on the page opens an SSE connection
   to the **same URL** (`/`) with `Accept: text/event-stream` → dashboard stream →
   connect-resync snapshot + live deltas (morph).
2. `POST /games/{id}` (move) → 202 → board change → broadcast → both that game's stream and
   the dashboard stream patch the board **by id**.
3. **No JS / no SSE:** the html base is current state; **refresh re-GETs current state**
   (the re-fetch-by-handle path).

### Accessibility (the point of this milestone)
- Server-rendered dashboard = discoverable + actionable for agents and screen readers with
  zero JS. Position-labeled controls already exist (`aria-label` per square).
- Add **`aria-live="polite"`** region(s) on the boards so morph-in-place updates are
  announced to screen-reader users on the JS path; the refresh fallback covers non-live
  consumers.

## Feasibility spike (do FIRST, gates the rest)

**Confirm Datastar will open an SSE connection against the same page URL** (`/`,
`/games/{id}`) with `Accept: text/event-stream`, rather than requiring a dedicated endpoint.
- If yes: proceed with same-URL negotiation as designed.
- If no: fallback — resources still content-negotiate, but Datastar points at the same path
  via an explicit marker/attribute (e.g. a query flag or a `data-*` the server reads
  alongside `Accept`). Keep the *resource* model (one handle) intact; only the client wiring
  differs. Spike output: a one-paragraph note in the implementing PR + the chosen wiring.

## Thesis-first E2E (write these FAILING before the issues)

Target `src/TicTacToe.Web.Tests/` (see `test/CLAUDE.md` — server on :5228, set
`TEST_BASE_URL`).

1. **Discovery (no-JS):** `GET /` with `Accept: text/html` (no SSE) returns a body
   containing all active games' boards with playable position forms — not an empty shell.
2. **html representation of a game:** `GET /games/{id}` `Accept: text/html` returns the
   current board server-rendered.
3. **event-stream representation:** `GET /` and `GET /games/{id}` with
   `Accept: text/event-stream` open a stream whose first payload is the current state.
4. **Live morph (JS path):** a move POSTed to `/games/{id}` is delivered to both that game's
   stream and the dashboard stream and patches the board by id (no duplicate board, no
   container clear).
5. **No-JS freshness:** after an opponent move, a fresh `GET` (refresh) of the html
   representation shows the new mark.
6. **a11y:** rendered boards carry `aria-live` and position-labeled controls.

## Issues (acceptance criteria each)

**Sequencing:** Spike → 1 → 2 → 3 → 4 → 5. Issue 2 depends on 1 (negotiation needs a
server-rendered html branch to negotiate to).

### Issue 1 — Dashboard server-render on `GET /` (html)
- `Handlers.home` renders the layout **plus all active games** server-side via
  `renderGameBoard`; `homePage` template takes the games list instead of an empty container.
- **AC:** no-JS `GET /` body contains every active game's board + position forms (E2E #1 green).
- **AC:** existing home behavior (create-game affordance gated by `MaxGames`) preserved.

### Issue 2 — Handler-level `Accept` negotiation for `/` and `/games/{id}`; retire `/sse`
- Each `get` branches: `text/event-stream` → stream handler; else → html render.
- Remove `resource "/sse"` and its route registration.
- **AC:** `Accept: text/html` → html; `Accept: text/event-stream` → a stream; default
  (no/`*/*` Accept) → html (E2E #2, #3 green).
- **AC:** `/sse` no longer routed; nothing references it.

### Issue 3 — Per-resource streams + `SseBroadcast` per-game filtering; connect-resync
- Dashboard stream = all games; per-game stream = one game (subscriber filtered by `gameId`).
- On connect: send full live snapshot per game, then forward deltas.
- **AC:** a per-game stream receives only that game's events; the dashboard stream receives
  all (unit/integration test on `SseBroadcast`).
- **AC:** connect after a move shows current state (E2E #3 includes post-move connect).

### Issue 4 — Morph-by-id on connect + broadcast (drop the container clear)
- Stream connect and move-broadcast both send id'd board patches (Datastar morph); remove
  the `#games-container` clear (Handlers.fs:148).
- **AC:** a move patches the existing board element in place — no duplicate board, no clear
  (E2E #4 green; assert single board element per game after a move).

### Issue 5 — `aria-live` regions + a11y E2E; confirm no-JS refresh fallback
- Add `aria-live="polite"` to the board template; verify position-labeled controls.
- **AC:** E2E #5 (no-JS refresh shows opponent move) and #6 (a11y attributes present) green.

## Out of scope (non-goals)

- Last-Event-ID / versioned replay (future optimization; connect-resync is sufficient now).
- Removing the Simple app (see follow-on milestone).
- Changing the engine, CQRS 202+SSE command path, or `broadcastPerRole` semantics.
- Any orchestrator/experiment changes.

## Follow-on milestone (separate spec, after this lands)

**"Absorb Simple as Proto's html representation."** Conneg makes Proto's `text/html` path
functionally equivalent to the Simple app (server-rendered, form-POST, refresh-to-update).
To retire Simple:
- Bring Proto's html render to **Simple's correctness** — notably **no affordances out of
  turn** (Simple disables the whole board off-turn, by design). Today Proto differs; this
  must match before Simple can be deleted.
- Delete `src/TicTacToe.Web.Simple` + its tests; repoint anything that referenced it.
- Update the orchestrator surface taxonomy: `Variant = Proto | Simple | ERPC` collapses —
  "Simple" becomes "Proto requested as `text/html`"; "Proto" becomes "Proto as
  `text/html` + event-stream (JS)". Update `Variant`, `surfaceOf`, matrices, server-start.
- Net: one app, conneg'd representations = F0 (html base) + enhancement (stream); ERPC stays
  the RPC null hypothesis. Cleaner expression of the accessibility thesis.

## Risks

- **Datastar same-URL SSE** (mitigated by the spike + fallback wiring).
- **Markup parity** between server render and stream patch — mitigated: both go through
  `renderGameBoard`.
- **Double-subscribe** (`getGame` and stream both call `subscribeToGame`) — assert
  idempotency.
