# Cold-Start Discovery Reset — Experiment Design

**Date:** 2026-06-23
**Status:** Design (approved, pre-implementation)
**Supersedes the framing of:** `experiments/HYPOTHESIS.md` (accessibility ≈ agent-ability) and aligns with `~/Code/frank/docs/AGENT_HYPOTHESIS.md` (discovery / iteration-cost).

## 1. Thesis

From a **cold start** — given only a URL and an abstract goal ("review the app at this URL and interact with it") — an agent should **recognize → interact → pursue** the app's goal using only the app's discovery surface, *even though the app is multiplayer with server-assigned roles*.

Hypothesis: progressively adding **affordance**, **accessibility**, **semantic-discovery**, and **ontology** layers moves an agent across those three bars; and the richer the surface, the **smaller the model** that suffices — a leaner model on a rich surface matches a heavier model on a bare one.

MCP/RPC is **not** a cold-start peer. An RPC/MCP client receives its contract at connection (the `tools/list` handshake), so it discovers by a different mechanism and does not play by the same rules. It is a *reference paradigm* (ceiling), not an arm run under cold-start conditions.

### What "discover" means here

Two-moment recognition, because role is **server-assigned by arrival order** (1st caller → X, 2nd → O, rest → observer) and no agent knows its role until it interacts:

- **Pre-assignment:** "this is tic-tac-toe; it is a two-player game; the goal is win/draw; here is how to take a seat."
- **Post-assignment:** "I was assigned O / I am an observer → these are *my* affordances." The **observer** correctly recognizing it has **no move affordance** is the headline role-projection proof.

## 2. Independent variables

### 2.1 Surface factors — 2⁴ full factorial (16 cells)

One app, four **independent toggles** → 16 served surfaces. Each factor isolates one variable.

| Factor | OFF | ON | Frank F-map |
|---|---|---|---|
| **A** — Affordances | naive (Simple: all cells rendered, occupied disabled, no explicit turn/actions) | correct (Proto: explicit turn + enumerated available actions in served HTML) | local-doc "structure" band |
| **C** — Accessibility | no ARIA | ARIA roles, landmarks, `aria-live`, labels, semantic structure | local-doc "a11y" band |
| **Sd** — Semantic discovery | plain HTML/JSON | `Allow` (state-dependent), `Link` rel=profile/self, OPTIONS, ALPS, JSON Home | F1–F4 |
| **So** — Semantic ontology | none | schema.org/Game typing, JSON-LD context to broader vocab, game classification (m,n,k-game family), sibling-game relations, strategy/constraint annotations, PROV-O provenance, SHACL validation | F6–F8 |

**Ontology is domain linkage, not richer discovery.** `Sd` tells the agent how to navigate *this app*; `So` tells it *what this app is in the world* and lets it borrow external world-knowledge ("this is the tic-tac-toe / m,n,k-game family; optimal strategy is known"). Load-bearing claim: **a beginner becomes an expert through ontology, never through bare discovery.**

Cell notation: 4-bit `A C Sd So` (e.g. `0000` = Simple floor, `1111` = full). `So=1, Sd=0` is a real cell — rich semantic *bodies* without discovery *navigation* ("can an agent use linked-data it stumbles into, unguided?").

Presets: **Simple** = `A=0`; **Proto (no-JS)** = `A=1`.

### 2.2 Model ladder (crossed dimension)

Descends from the proven anchor. **No Sonnet** — Haiku-4.5 already proved capable in the existing harness.

`Haiku-4.5 (anchor/top)` → `mid OSS` → `small OSS (gemma-4-e4b / llama-3.1-8b)`. OpenRouter-swappable.

## 3. Dependent variables

The factor→outcome mapping splits the DV into two axes:

| Factor | Drives axis |
|---|---|
| **A** affordances | **game-play** (won't help find the app; won't let you misplay it) |
| **C** accessibility | **discovery** |
| **Sd** discovery-semantics | **discovery** |
| **So** ontology | **BOTH** — leverage varies by model (a key unknown, not a nuisance) |

Recorded per party, every run:

- **Recognize** (discovery axis) — graded 2-moment discovery report `{appIs, goal, isMultiplayer, howToJoin}` (pre) and `{myRole, myAffordances, canIMove}` (post-assignment) vs ground truth; **+** first-action coherence (secondary; catches knew-but-stuck vs lucky-guess); **+** role-discrimination (X moves on X-turn, O cannot; observer never can).
- **Interact** (game-play axis) — accepted vs rejected moves, rejection codes actually received.
- **Pursue-completion** — outcome (win / draw / abandoned), moves-to-terminal, turns, tokens, time. Abandoned ≠ slow-but-progressing.
- **Pursue-quality** (game-play axis) — blunder rate / distance-from-optimal move. *Where ontology's beginner→expert effect lands; completing a game ≠ playing it well.*
- **Friction** — read:write ratio, 4xx counts (from `experiments/.../proxy.py`).

## 4. Fixed setup (all cells, all runs)

- **3 cold-start discovery agents** connect in a **harness-sequenced fixed order** → server assigns X / O / observer by arrival. Agents do not know role until they interact.
- Each game yields **3 role-observations** (X-agent, O-agent, observer-agent).
- **Discovery instruction held constant** across every cell and arm — what varies is *what is there to discover*, never the prompt scaffold.
- **Same paths** across all surfaces and the V_swagger bracket (dissolves path-name contamination).
- **Harness:** 3 agents × a **generic HTTP tool** (GET/POST any URL); pre-baked endpoints stripped from the `oss-driver` ReAct shape so the run is genuinely cold-start. MCP arm uses native `tools/list`.
- **Ground-truth** for grading authored once per app from the statechart / spec.

## 5. Program

### Phase 1 — Attribution (16-cell factorial @ Haiku-4.5 anchor)

Run all 16 cells at the anchor model. Establishes main effects + interactions and the factor→axis mapping. Tells the titration *which path through the cube is worth walking*, instead of all 16 at every model.

- **N = 5 games / cell** (16 × 5 = 80 games, 240 role-observations).
- Calibrates the blunder-rate scale → sets threshold τ for Phase 2.

### Phase 2 — Titration / crossover (the headline)

Descending titration, not a full grid: for a given surface, start capable → drop model size until it breaks → record the **floor model** (smallest sufficient). Enrich surface, repeat. Walk surface richness up the path Phase 1 flagged as meaningful (corners `0000`/`1111` + cumulative diagonal + any inflection cells).

- **Titration success threshold:** recognize-bar met **AND** pursue-completion **AND** blunder-rate ≤ τ.
- **N = 5** games per model-rung per surface.
- **Output:** `floor-model(surface-richness)` frontier, expected monotone-decreasing.
- **Named test:** *gemma-4 @ `1111` ≈ Haiku-4.5 @ `0000`*.

### Brackets (later, external reference points)

- **V_swagger** — same paths, OpenAPI mounted, role-uniform, no state-dependent affordances, no role projection. Floor; sits *outside* the cube (role-uniform vs the cube's role-projected).
- **ERPC** — MCP, discovery-on-connection. Ceiling; separate paradigm.

## 6. Pre-registered predictions

Stated before any run.

| # | Prediction | Factorial / titration test |
|---|---|---|
| P1 | Recognition gates everything — once it knows what it's playing, it does okay | recognize-bar predicts pursue across cells |
| P2 | Affordances help game-play, not discovery | **A**: large main effect on interact + pursue-quality; ~0 on recognize |
| P3 | Accessibility is the surprise on discovery | **C**: main effect on recognize > 0 (a11y as weak-semantic cousin) |
| P4 | Discovery-semantics drives discovery | **Sd**: large main effect on recognize |
| P5 | Ontology lifts game-play *quality*, most on weak models | **So**: main effect on blunder-rate; largest at low model rungs |
| P6 | a11y = weaker cousin of semantic web | **C × Sd** sub-additive on recognize (partial redundancy); C-alone < Sd-alone but C > 0 |
| P7 | All together, most capable, by large margins | **A × C × Sd × So** super-additive (`1111` ≫ best single factor) |
| P8 | Richer surface buys back model size | iso-performance frontier: leaner-model-rich ≈ heavier-model-bare |

## 7. Validity guards

- Discovery instruction constant across all rungs/arms.
- Same paths V_swagger ↔ ladder app.
- Same game, same terminal condition, every cell.
- Role assignment deterministic via harness-sequenced connection order.
- Surface factors toggled from one codebase → no cross-app implementation drift.

## 8. Open / deferred

- Concrete mid/small OSS model picks (OpenRouter-swappable; settle at Phase-2 start).
- Blunder-rate / distance-from-optimal scoring source (tic-tac-toe is solved → exact optimal table available).
- V_swagger + ERPC bracket runs scheduled after Phase 1–2 land.
- Streaming round (SSE small-deltas) — out of scope for this reset; request/response only.
- **Seed back-off (deferred — not now).** Identity is currently driver-owned: the harness does a `GET /login` (cookie jar) then feeds the agent the served `GET /` body as its first observation (`Driver.fs` `runSeat`, lines 67–77). Preferred end-state is to *remove* the driver-owned `/login` and let the agent discover auth itself via a `302 → /login` redirect on the protected resource. Kept as a constant contract mechanic across all 16 cells for now so it is **not** a confound; revisit before bracket runs, not during SP2.
