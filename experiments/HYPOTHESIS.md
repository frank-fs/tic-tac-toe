# Experiment Hypothesis

How well can an LLM agent *play* a game it discovers through a web interface,
versus through purpose-built RPC tools — and what about the interface design
decides the outcome?

## Core thesis: accessibility ≈ agent-ability

A web UI encodes meaning in three layers:

| Layer | Carries | A text/HTTP agent gets it? |
|-------|---------|----------------------------|
| **Structure** (HTML/DOM) | elements, forms, links | yes |
| **Behavior** (JS, e.g. datastar) | clicks → POSTs, live patches | only with a JS engine |
| **Presentation** (CSS) | turn highlight, colour, layout | no — invisible, and it bloats context |

An agent is a non-visual consumer — like a screen reader. The barriers a
screen-reader user hits (meaning trapped in CSS, behaviour trapped in JS, no
semantic affordances) are the same barriers an agent hits. So:

- the agent's contract should be the **accessibility tree** (post-render,
  semantic, CSS-stripped);
- meaning must live in semantics/ARIA, never CSS-only (this is just WCAG);
- **building a hypermedia API an agent can use is the same discipline as
  building an accessible web app.**

## The regressive burden

A capable model can brute-force an inaccessible JS app by orchestrating many
tools (snapshot, click, wait, inspect-network) and holding that state across
turns. A small model drowns in the coordination. So **inaccessibility excludes
the least-capable agents** — exactly as poor accessibility excludes the users
with the least capacity to work around it. The interesting result is therefore
an **interaction effect**, not a main effect: *does progressive enhancement
close the small-model gap?*

## Variants (the same game, three interfaces)

| Variant | Interface | Notes |
|---------|-----------|-------|
| **ERPC** | purpose-built MCP tools (`list_games`/`get_state`/`make_move`) | RPC null hypothesis; structured state |
| **Simple** | server-rendered HTML, form POST | classic, no-JS; intentionally naive (renders all cells, disables occupied) |
| **Proto** | reactive HTML, datastar + SSE, CQRS/event-sourcing | move = command (`202`); board = projection (SSE) |

Agents drive both Simple and Proto through `browsegrab` — a token-efficient browser
tool that renders the page and projects its **accessibility tree** (semantic controls,
labels, states, stable `eN` refs). It realises the core thesis' contract — the agent
consumes the a11y tree — instead of making the agent parse raw HTML itself.

> **Preferred future direction (non-browser):** Simple and Proto are progressively
> enhanced — forms and links live in the *served* HTML, no JS render required — so an
> affordance-projecting client could deliver the same legible a11y-tree surface *without*
> driving a headless browser. browsegrab is the working interaction method today; the
> lighter-weight non-browser projector remains the goal.

## Hypotheses under test

1. **Tooling fairness** — when the web agent reads a legible accessibility-tree
   surface (browsegrab) comparable to ERPC's structured tools, the remaining gap is
   representation legibility + model capability, not the tooling. *(Established: the
   early "ERPC wins / HTTP abandons" result was a tooling confound — a raw-HTML
   client put the affordance-extraction burden on the agent.)*
2. **SSE small-deltas help small models** — event-sourced projections pushed over
   SSE are small updates → less context per turn than re-reading a full page → help
   a small model get further. Tested in a later **streaming** round; this round is
   request/response (snapshot → act → re-snapshot), no live stream.
3. **Progressive enhancement democratises agent access** — Proto's enriched,
   progressively-enhanced semantics (explicit turn + affordances in the served HTML,
   command via plain form) let a small model play where Simple's intentionally-naive
   HTML leaves it stalling. The headline interaction effect. *(The non-browser
   affordance-projector above is what would let this run without any browser at all.)*

## Axes

- **Interface**: ERPC / Simple / Proto.
- **Model size**: small (e.g. `gemma-4-e4b`) vs capable (e.g. a ~14B) — to surface
  the regressive burden.
- **Affordance richness** (future F1–F8): semantic/ARIA layers — roles, landmarks,
  `aria-live`, link relations, ALPS. "Discovery" is reading accessibility
  affordances.

## Measures

Outcome (won / draw / abandoned), accepted moves, rejection codes the agent
actually receives, turns and tokens per game, time-to-completion. Abandonment is
not a hard failure — a slow-but-progressing game truncated by the per-cell cap is
distinct from a genuine stall.
