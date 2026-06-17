# ERPC identity handshake — design (#67)

**Date:** 2026-06-16
**Issue:** #67 — [IR] ERPC arm lacks an identity handshake — confounds the identity/authorization comparison with HTTP
**Milestone:** Interaction reliability (prerequisite)
**Arm:** `experiments/mcp-rpc` (RPC-over-MCP, stdio transport)

## Problem

The HTTP/Web arm forces an identity handshake and binds identity to a seat:

- `GET /` is `requireAuth` → `/login` mints a persistent `SameSite=Strict` cookie (a stable identity).
- `PlayerAssignmentManager` binds that identity to an X/O seat on first move.
- The server enforces it via `TryAssignAndValidate` / TurnGuard: claiming the wrong seat is rejected (`NotYourTurn` / `NotAPlayer`).

The ERPC arm (`experiments/mcp-rpc/Tools.fs`) has no equivalent. `make_move(gameId, player, position)` takes `player` ('X'/'O') as a **free, self-asserted parameter** — no identity, no token, no caller→seat binding, no authorization. The ERPC agent carries none of the identity burden the HTTP agent must satisfy, so any discovery/identity comparison between arms is confounded.

The defect is not "no token" — it is the **self-asserted `player` argument**. The fix: the caller proves identity once; the server derives the side.

## Scope

API-design-oriented, not full ambient-session parity. In scope:

- Add an `authenticate()` handshake tool.
- Remove the self-asserted `player` argument from `make_move`.
- Present identity as a **claim** (not a move-payload parameter); server derives the seat.
- Lazy per-game seat binding mirroring the Web arm's decision table.

Out of scope: stateless signed tokens (JWT), HTTP-transport auth, changes to the Web/Simple/Proto arms.

## Design

### Tool surface

| Tool | Signature | Change |
|------|-----------|--------|
| `authenticate` | `authenticate()` → `{ token }` | **New.** Mints an identity token; binds it to this connection's session claim. No game yet. |
| `make_move` | `make_move(gameId, position)` → board/error | **`player` removed.** Move payload only; identity comes from the claim. |
| `new_game` | `new_game()` → `{ gameId }` | Unchanged. Creation carries no identity assertion. |
| `get_board` | `get_board(gameId)` | Unchanged. Reads carry no identity assertion. |
| `get_state` | `get_state(gameId)` | Unchanged. |

### Identity / claim channel

stdio has no built-in authentication and no auto-populated principal (unlike the HTTP transport). Identity is carried as a **claim**, established by the handshake and presented per request — never as a `make_move` argument:

1. `authenticate()` mints a token (opaque id) and stores it as the connection's session identity.
2. An **incoming message filter** (`WithMessageFilters` → `AddIncomingFilter`) reads the connection's session identity and sets `context.User = ClaimsPrincipal(token)` on every request — the SDK's documented stdio identity pattern.
3. `make_move` declares a `ClaimsPrincipal` parameter, which the SDK injects and **excludes from the tool input schema**. The server derives the token from the claim → seat → side.

stdio = one process per agent = one connection = one session, so the claim unambiguously presents that agent's token. The agent authenticates once (≈ HTTP login), then plays without re-presenting credentials (≈ cookie auto-attached) — no per-call token-copy friction.

### Seat binding (lazy, per game)

First `make_move` for a given `(token, gameId)` binds the token to an open seat; subsequent moves validate ownership and turn. Logic mirrors the Web `PlayerAssignmentManager.TryAssignAndValidate(gameId, userId = token, isXTurn)` decision table (`src/TicTacToe.Web/Model.fs:58-103`) exactly:

- X slot open and X's turn → assign token as X → allowed.
- O slot open, O's turn, token ≠ X → assign token as O → allowed.
- Token is X and X's turn (or O and O's turn) → allowed.
- Token owns a seat but it is the other side's turn → `not_your_turn`.
- Both seats bound to other tokens → `not_a_player` / `game_full`.

A token may hold seats across multiple games; binding is keyed by `(token, gameId)`. The Web `PlayerAssignmentManager` lives in `TicTacToe.Web`, which the ERPC project does not reference (it references only `TicTacToe.Engine`), so an equivalent thread-safe store is built inside the ERPC arm using the same MailboxProcessor pattern.

### Error codes

| Code | Trigger | HTTP-arm analogue |
|------|---------|-------------------|
| `unauthenticated` | `make_move` with no claim (before `authenticate`) | `requireAuth` redirect to `/login` |
| `not_your_turn` | token owns a seat, wrong turn | `NotYourTurn` |
| `game_full` / `not_a_player` | both seats bound to other tokens | `NotAPlayer` |
| `game_not_found` | unknown `gameId` | (existing) |
| `position_taken` | occupied square | (existing) |
| `invalid_input` | unparseable position | (existing) |

### Structure

`TicTacToeTools` changes from a static type over a module-level `supervisor` to an **instance type** with constructor-injected state: `supervisor`, a session-identity holder, and the assignment store. Registered as a singleton via DI; `WithTools<TicTacToeTools>()` resolves it. This removes the module-level mutable `supervisor` (Holzmann R13) and gives the session/assignment state a lifetime owner. Change is contained to `experiments/mcp-rpc`.

## Components

- **`SessionIdentity`** — per-connection holder for the authenticated token (set by `authenticate`, read by the message filter). One per stdio connection.
- **`PlayerAssignmentStore`** — `(token, gameId)` → seat binding + validation; mirrors the Web decision table; thread-safe MailboxProcessor.
- **Message filter** — populates `context.User` from `SessionIdentity` each request.
- **`TicTacToeTools`** (instance) — `authenticate`, `make_move` (claim-injected), `new_game`, `get_board`, `get_state`.

## Data flow (make_move)

```
agent → make_move(gameId, position)
  message filter sets context.User from SessionIdentity (the token)
  SDK injects ClaimsPrincipal into make_move
  no claim?                      → { error = "unauthenticated" }
  token = claim.Identity.Name
  PlayerAssignmentStore.TryAssignAndValidate(gameId, token, isXTurn)
    Rejected NotYourTurn         → { error = "not_your_turn" }
    Rejected NotAPlayer          → { error = "game_full" }
    Allowed → derive side (X|O) → construct XMove|OMove pos → game.MakeMove
      Engine Error               → { error = "position_taken" | ... }
      ok                         → { board, whoseTurn, status }
```

## Testing

- `authenticate` returns distinct tokens across calls.
- `make_move` before `authenticate` → `unauthenticated`.
- Two authenticated tokens in one game: first binds X, second binds O; correct turn alternation; out-of-turn move → `not_your_turn`.
- Third token attempting to play a full game → `game_full` / `not_a_player`.
- One token across two games: independent seat bindings (multi-game).
- `make_move` no longer accepts a `player` argument (schema/signature change).

## Confound note (for the experiment log)

The ERPC arm now mirrors HTTP's ambient-identity property: one explicit handshake (`authenticate` ≈ login), then identity rides the claim automatically (≈ cookie). The earlier token-as-argument sketch was rejected precisely because it would have introduced a *new* asymmetry — manual per-call token copy that the HTTP agent never performs (and a known small-model failure mode). The claim channel removes that friction while still discharging the identity burden #67 requires.
