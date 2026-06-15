# L3 Findings: the browser–RPC gap is interface-representational

Companion to [HYPOTHESIS.md](./HYPOTHESIS.md). Records the 2026-06-14/15 work that
established **agent interaction reliability** as a prerequisite to the F0–F8 discovery
study, and the instruction-titration (L3) experiment that pinned the browser↔RPC gap to
the interface representation — not the model, the instruction, or the time budget.

## Reframe

The goal is **reliable interaction** (the agent gets the right options and acts as
intended), not gameplay success. You can't measure discovery-layer effects if the agent
can't reliably observe and act on the app. So: find a surface that gives ERPC-like
reliability first; *then* accessibility becomes the controllable dial.

## Surfaces compared

Same engine underneath. Three agents per game (two players + one observer) — the
**two-player cross-browser refresh** (X acts → O's separate session must re-read to see it)
is the core thing under test; the observer probes the blocked path.

- **Simple via browser** (`browsegrab` MCP — token-efficient a11y-tree tool for local LLMs,
  stable `eN` refs, clicks by submitting page forms).
- **ERPC** (`src/TicTacToe.Mcp` — RPC tools: `list_games` / `join_game` / `get_state` /
  `make_move`).

Model: `google/gemma-4-e4b` via LM Studio. Analyzer: `experiments/scripts/classify_moves.py`.

## Finding 1 — the original ERPC was a worse asymmetry, now fixed

Emergent N=5 (`simple-ab`): browser **5/5 stalled at exactly 1 move** (zero variance);
ERPC **2 won / 1 draw / 2 abandoned**. The gap was **not the tool** — the old ERPC
`make_move(gameId, position)` took no player and applied to whoever's turn it was, so **one
agent puppeted both X and O** (verified: the same agent placed both marks). It required
*zero coordination*. Simple binds roles to sessions (correct), so it needs real two-player
coordination.

→ Rebuilt ERPC for parity: `join_game(gameId, preferredRole)` returns a `playerToken`
(third claimant → `GameFull`); `make_move(gameId, playerToken, position)` enforces
token→role + turn (`NotYourTurn` / `NotAPlayer`). Role state lives in a **singleton
`PlayerRegistry`** — the MCP framework resolves a fresh tool instance per request, so
tool-instance fields don't persist (only the `GameSupervisor` singleton did; that was the
bug). Smoke-tested: scripted X-win → `won, game_over` fires.

Both surfaces deliver refreshes correctly. Simple shows the full board out of turn and only
**disables the buttons** (no out-of-turn affordances — by design); it does not hide state.

## Finding 2 — L3 instructed ladder: browser never completes; ERPC always does

L3 = maximal instruction: scripted role + exact move sequence + explicit per-turn
procedure (X wins in 5). Failure layers peeled one per iteration, each fix confirming the
prior blocker:

| Iter | Change | Browser result | ERPC |
|------|--------|----------------|------|
| v1 | scripted personas | scriptedO read "wait" as **stay in lobby** → 0 useful | won |
| v2 | "open the game first" | entered arena but submitted a **stale form** → 1 mark | won |
| v3 | reload-before-move | staleness fixed, O landed → **2 marks** | won |
| v4 | turn-alternation + persistence | clean, **0 errors**, but throughput-bound → 2 marks | won |
| v5 | cap 300s → 900s | more time → **3 marks**; still abandoned | won (184s) |

- **Stale-form root cause:** Simple's handler builds the move from the form's hidden
  `player` field, baked in at *render time*. An agent acting on a page even one
  opponent-move old submits stale identity → engine `Error` (mislabeled `PositionTaken`).
  Refs are **stable** (`e5`=MiddleCenter before/after) — not ref-churn. Fix = reload before
  acting / on rejection (the natural human reaction).
- **Vercel agent-browser would not fix this:** same snapshot-ref model, "no automatic
  page-state refresh." The fix is procedural (reload-before-act), tool-independent.
- **Throughput:** browser lands ~1 move per ~300s (≈8× ERPC's ~37s/move); a 5-move game
  needs ~1500s+. Agents also leak (observer attempted 7 moves vs the instructed 1; O over-
  reads). More time helps only **linearly**.

## The core mechanism — why ERPC re-reads and the browser doesn't

Both surfaces hand the agent a *prior* view it could reuse. ERPC re-pulls `get_state`
before every move anyway; the browser acts on a stale page. Why?

**ERPC surfaces `whoseTurn` as an explicit, legible datum the agent tracks.** It knows the
opponent moves between turns, so its cached board is self-evidently stale → it must
re-query to see the turn flip back. Re-pulling is the mechanical consequence of tracking
the turn variable. And the poll→act loop *is* the entire interaction — there's nothing else
to do — so the agent stays in it.

**The browser buries the same dynamics in a rendered document.** There's no `whoseTurn`
variable being watched; there's a board that reads as "the current game." The
wait-reload-retry loop is *extra discipline layered on "see page, click"* — and the agent
leaks out of it the moment the page looks actionable, at a different point each run.

Same model. The **interface** induces the freshness discipline: pull-by-handle is
self-freshening; persistent-document caches and misleads.

## Conclusion

At L3 (max instruction) and 3× time, the browser surface makes slow partial progress but
**does not complete under any realistic budget**, while ERPC completes reliably and cheaply.
The gap is **interface-representational** — implicit state/turn/identity force a heavy,
slow, leak-prone per-move loop; explicit + live state gives a lean, self-sustaining one.
More instruction and more time move the needle only linearly; they do not close it.

This is the empirical case **for** the original linked-data + statecharts goal: a
representation that surfaces turn + affordances as explicit, legible, live state — making a
browser agent's loop lean and self-sustaining the way `whoseTurn` does in RPC. That is the
dial the F0–F8 study needs before it can isolate discovery-layer effects.

## Keepers / next

- **Keepers:** ERPC role enforcement (`join_game` + `playerToken` + `PlayerRegistry`);
  `browsegrab` wired as a Browser surface (`surfaceOf`); `classify_moves.py`; L3 scripted
  personas + `instructed` / `simple-ab` matrices.
- **Open directions:** (a) build the linked-data/statecharts representation; (b) titrate
  ERPC down (L2/L1/L0) for the instruction-floor reference curve; (c) re-evaluate Proto's
  implementation design before testing Proto (SSE re-render adds async stale-ref stress on
  top of all of the above).
