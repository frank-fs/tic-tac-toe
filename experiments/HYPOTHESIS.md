# Experiment Hypothesis

Can an agent, given **only a URL and an abstract goal** ("review this app and
interact with it"), **recognize** what the app is, **interact** with it
correctly, and **pursue** its goal — *even though the app is a two-player game
with server-assigned roles*? And does enriching the app's surface let a
**smaller model** do what previously took a larger one?

> Full design, measurement model, and pre-registered predictions:
> [`docs/superpowers/specs/2026-06-23-cold-start-discovery-reset-design.md`](../docs/superpowers/specs/2026-06-23-cold-start-discovery-reset-design.md).

## Core thesis: legibility ≈ agent-ability ≈ affordable-ability

A cold-start agent is a non-visual consumer, like a screen reader. The meaning
it needs lives in layers, and each layer is a thing we can switch on or off:

| Layer | Carries | Drives which bar |
|-------|---------|------------------|
| **Affordances** (A) | forms, links, explicit turn + available actions | game-play (won't help *find* the app; won't let you *misplay* it) |
| **Accessibility** (C) | ARIA roles, landmarks, `aria-live`, semantic structure | discovery (the suspected surprise — a11y as weak-semantic) |
| **Semantic discovery** (Sd) | `Allow`/`Link`/ALPS/JSON Home — how to navigate *this app* | discovery |
| **Ontology** (So) | schema.org/Game, JSON-LD, classification, sibling-game + strategy links — what this app *is in the world* | **both** — and a beginner becomes an expert through ontology, never through bare discovery |

Building a hypermedia API an agent can use cold is the same discipline as
building an accessible web app — and ontology is what lets the agent reach past
the app into the broader domain it already knows.

## What's under test

A **2⁴ factorial** (A × C × Sd × So → 16 served surfaces from one app's
toggles), crossed with a **descending model ladder** (Haiku-4.5 anchor → OSS).
Every run is **multiparty**: three cold-start agents connect, the server assigns
X / O / observer by arrival order, and no agent knows its role until it
interacts. The **observer** — which must recognize from discovery alone that it
can *only watch* — is the sharpest proof that affordances are used correctly.

The headline is an **interaction effect**, not a main effect: *all layers
together, by large margins* (super-additivity), and a **cost-efficiency
crossover** — a leaner model on a rich surface matching a heavier model on a
bare one (e.g. `gemma-4 @ full ≈ Haiku-4.5 @ Simple`).

## Variants and brackets

| Variant | Role |
|---------|------|
| **The 16-cell app** | the factorial — Simple = affordances-off preset, Proto (no-JS) = affordances-on preset |
| **V_swagger** | floor bracket — same paths, OpenAPI, role-uniform, no state-affordances; *outside* the cube |
| **ERPC** | ceiling bracket — MCP, discovery via `tools/list` handshake; a *reference paradigm*, not a cold-start peer (RPC doesn't play by the same rules) |

## Measures

Per party, per run: **recognize** (graded 2-moment discovery report +
first-action coherence + role-discrimination), **interact** (accepted/rejected +
codes), **pursue-completion** (won/draw/abandoned, turns, tokens, time),
**pursue-quality** (blunder rate / distance-from-optimal — where ontology's
beginner→expert effect lands), and **friction** (read:write, 4xx). Abandonment
is not a hard failure — a slow-but-progressing game is distinct from a stall.

---

*Prior framing (accessibility-only, browsegrab-coupled, three-arm) is
superseded by this reset; accessibility survives as factor **C**. The streaming
round (SSE small-deltas) is out of scope here — request/response only.*
