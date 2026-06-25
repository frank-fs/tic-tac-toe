# SP2 — Surface Factorial Binary — Design

**Date:** 2026-06-25
**Status:** Design (approved, pre-implementation)
**Parent spec:** [`2026-06-23-cold-start-discovery-reset-design.md`](2026-06-23-cold-start-discovery-reset-design.md) — this realizes §2.1 (the 2⁴ surface factorial) as a single configurable binary.
**Scope:** SP2 builds the 16-cell cube only. Brackets (V_swagger floor, ERPC ceiling) and the model ladder are later SPs.

## 1. Thesis for this SP

One binary serves any of the 16 factorial cells, selected at startup by a 4-bit
cell flag `A C Sd So`. Each factor is an **independent conditional** on a parsed
`Surface` record — no per-cell template trees, no second codebase, nothing that
can drift between cells. "Simple" and "Proto" stop being separate apps and
become **presets** of this one binary (`0000` and `1000`).

This directly satisfies the parent spec's §7 validity guard ("surface factors
toggled from one codebase → no cross-app implementation drift"): there is now
exactly one codebase.

## 2. What is being built

Evolve the existing `experiments/src/TicTacToe.Web.Simple` **in place** into
`experiments/src/TicTacToe.Web.Surface`. It is already cell `0000` — naive HTML,
cookie auth, `GameStore`, `PlayerAssignmentManager`. SP2 adds:

1. A `Surface` config record parsed once at startup.
2. Four conditional rendering/header branches keyed off `Surface`.
3. Removal of the always-on `useOpenApi` (OpenAPI is V_swagger's signature, a
   later bracket *outside* the cube — see §6).

**Dependency discipline:** the binary depends on **only `TicTacToe.Engine`** (the
pure game lib) — no Frank-app, no main-`src` coupling — so the whole experiment
lifts to its own repo later as a *move, not a rewrite* (a deferred decision, see
§7).

## 3. Configuration

A single environment variable selects the cell:

```
TICTACTOE_CELL=ACSdSo      # 4 chars, each '0' or '1', order A,C,Sd,So
```

- Parsed **once at startup** into `Surface = { A: bool; C: bool; Sd: bool; So: bool }`.
- Held in a DI singleton; passed **explicitly** to handlers/renderers. No
  module-level mutable (Holzmann R13).
- **Unset** → default `0000` (the floor), so an un-flagged run is the baseline,
  never an accidental rich surface.
- **Set but malformed** (wrong length or non-`[01]` chars) → **fail fast** (R12):
  throw at startup with a clear message, do not boot a misconfigured surface.

Selection model is **one process per cell** (parent spec confirmed): the harness
boots the binary with `TICTACTOE_CELL`, runs N games, kills it. No per-request
cell routing, so no cross-cell contamination surface.

## 4. Factor payloads

Each factor is a pure conditional on `Surface`. Factors compose freely; the cube
is their product. All four apply to **both** the entry page (`GET /`, the agent's
seed observation) and the arena page (`GET /arenas/{id}`), because discovery must
be present on the first surface the agent sees.

### A — Affordances (rendering)

| | Served HTML |
|---|---|
| **A0** (current Simple) | All 9 cells as POST buttons; occupied disabled; **no turn statement, no role-awareness** — every caller sees move forms regardless of whose turn it is; server rejects out-of-turn on submit. |
| **A1** (Proto-correct) | Page **states whose turn it is**, and enumerates **only the requesting agent's currently-legal actions**. The non-turn player and the observer get **zero move forms**. |

The A axis is *turn/role-aware availability* — does the served HTML tell the agent
which moves are legal *for it, right now*. (Note: A0 already varies by board state
via occupied→disabled; that is **not** the A axis.) Concretely, `A1` must not fall
back to the `A0` spectator-defaults-to-"X" behavior in
`templates/game.fs:resolvePlayerStr` — an unseated caller under `A1` is an
observer and receives no move affordance.

### C — Accessibility

`C1` adds, with **no layout/markup-structure change** beyond attributes/landmarks:

- `role="grid"` on the board, `role="gridcell"` per square.
- `aria-label` per square (position + occupancy).
- Landmarks: `<main>`, `<nav>` for the back-link / controls.
- `aria-live="polite"` on the status region.
- Semantic headings already present (`h1`); ensure heading order is sane.

`C0` emits none of these. C is purely additive ARIA/semantics.

### Sd — Semantic discovery

`Sd1` adds app-navigation discovery (parent spec F1–F4):

- **Response headers:** `Link` (`rel="profile"` → `/profile`; `rel="self"`),
  state-dependent `Allow` on arena responses (reflects which methods/actions are
  valid given current state+role), and a real `OPTIONS` handler per resource.
- **`GET /profile`** — an ALPS document describing the app's affordances
  (take-seat, make-move, restart, delete) and their semantics. Adapt the shape of
  the main app's `wwwroot/alps/tictactoe.json` as a reference; emit the Surface
  app's own.
- **`GET /.well-known/home`** — a JSON Home document listing the resources and
  their relations.

`Sd0` emits plain HTML with none of the above headers/endpoints.

### So — Semantic ontology

`So1` embeds domain linkage in the served bodies (parent spec F6–F8), **trimmed to
what a cold-start LLM can actually consume**:

- `<script type="application/ld+json">` with a **schema.org/Game** typing block
  for the arena.
- **m,n,k-game classification** (tic-tac-toe = (3,3,3)).
- **Sibling-game relations** (e.g. Connect Four = (7,6,4), Gomoku = (15,15,5)) as
  links/annotations.
- **Strategy/constraint annotation** — the load-bearing one: "perfect play is a
  known draw; optimal strategy is solved" — so a weaker model can borrow external
  world-knowledge (tests parent-spec P5: ontology lifts play *quality*, most on
  weak models).

`So0` emits none.

**Excluded by design (not deferred):** SHACL shapes and PROV-O provenance from the
parent spec's §2.1 ON-list are **out of scope** — a cold-start LLM agent does not
run a SHACL validator or traverse a PROV-O graph, so they cannot move any DV.
Noted here for pre-registration honesty: the realized `So=1` is the agent-usable
subset, not the full §2.1 list.

## 5. Code structure

- `Surface.fs` (new): the record + `parse : string -> Surface` (fail-fast) +
  startup wiring into DI.
- `templates/game.fs`, `templates/home.fs`: render functions take an added
  `surface: Surface` param and branch (A, C, So affect bodies).
- `Handlers.fs`: inject `Surface`; thread to renderers; Sd headers set here;
  new `OPTIONS` handlers.
- `Program.fs`: new resources `GET /profile`, `GET /.well-known/home` (handlers
  early-return 404 when `Sd=0`, so the routes exist but the surface is honest);
  **remove `useOpenApi`**.
- Approach chosen: **single parameterized renderer with `Surface` branching**
  (linear conditionals, one indirection — R9/R15), *not* decorator-composition
  (extra indirection layers) and *not* per-combination templates (drift).

## 6. OpenAPI / V_swagger boundary

The current `Program.fs:149 useOpenApi` is removed from the cube. Per parent spec
§5, **V_swagger** is a *separate bracket outside the cube* — "same paths, OpenAPI
mounted, role-uniform, no state-affordances." OpenAPI is its distinguishing
feature, not a cube factor; leaving it always-on would mean `Sd=0` cells are not a
true discovery floor. V_swagger returns as its own preset in the bracket SP.

## 7. Deferred (banked, not built in SP2)

- **MCP / ERPC co-mount.** Architecturally the cube binary, V_swagger, and ERPC
  should share the one `TicTacToe.Engine` core, and MCP co-hosts on Kestrel via
  streamable-HTTP — so one binary *can* serve all three and retire the manual
  multi-process harness lifecycle. Not wired in SP2; brackets are a later phase.
  Seam preserved (shared core, Kestrel host); mount deferred.
- **Repo extraction.** The experiment (factorial app + harness + grading +
  hypothesis + results) is separable from the tic-tac-toe/Frank demo and should
  get its own repo eventually — **not** `frank-fs/frank` (a framework repo is the
  wrong home for an LLM factorial; at most a stripped "discovery sample" could
  live there as a showcase). SP2 only *enables* this by keeping the
  `TicTacToe.Engine`-only dependency; the move is a later decision.
- **Seed back-off.** Identity is currently driver-owned (`GET /login` cookie jar +
  served `GET /` as first observation). Preferred end-state: agent discovers auth
  via `302 → /login` redirect. Kept constant across all 16 cells for now so it is
  not a confound; revisit before bracket runs.

## 8. Testing

Per the SP's "TDD throughout" directive, **minimal targeted** unit tests (the
toggle logic is load-bearing for factorial validity — a C-attribute leaking into
an `A0`/`C0` cell would confound a main effect):

- `Surface.parse`: valid `0000`/`1111`/mixed; invalid length, bad chars, empty →
  fail-fast.
- Per-factor **present-iff-flag** assertions on rendered output: e.g. `aria-` only
  when `C=1`; `Link`/`Allow` headers + `/profile` 200 only when `Sd=1`;
  `application/ld+json` only when `So=1`; observer gets no move form when `A=1`.
- Not 16×full-render — a handful per factor. Live e2e runs remain the real
  validation.

## 9. Success criteria (verifiable)

1. `dotnet build` of the renamed project succeeds; harness wiring updated for the
   rename.
2. `TICTACTOE_CELL=0000` reproduces today's Simple floor byte-for-byte (modulo the
   removed OpenAPI mount).
3. `TICTACTOE_CELL=1111` serves: A1 role/turn-aware actions, C ARIA, Sd
   headers+`/profile`+`/.well-known/home`, So JSON-LD.
4. Each of A/C/Sd/So toggled alone flips exactly its own payload and nothing else
   (the present-iff-flag tests).
5. Invalid `TICTACTOE_CELL` fails fast at startup.
6. One live e2e run (Haiku-4.5 via OpenRouter) on a non-floor cell completes and
   the transcript shows the new surface (self-verified, not subagent-reported).
