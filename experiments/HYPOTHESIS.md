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

HTTP agents drive both Simple and Proto through `mcp-http/` — a hypertext-faithful
HTTP client that surfaces the HTTP envelope (status, headers) and holds
`text/event-stream` responses open, draining events as small deltas. No browser.

## Hypotheses under test

1. **Tooling fairness** — when the HTTP agent has protocol-native tools (envelope
   visible, streams drainable) equal to ERPC's, the remaining gap is
   representation legibility + model capability, not the tooling. *(Established:
   the early "ERPC wins / HTTP abandons" result was a tooling confound.)*
2. **SSE small-deltas help small models** — event-sourced projections pushed over
   SSE are small updates → less context per turn than re-reading a full page →
   help a small model get further. Testable browser-free once the write path is
   progressively enhanced.
3. **Progressive enhancement democratises agent access** — a PE app (command via
   plain form, projection via SSE) lets a small model play where the
   datastar-only / browser-required version cannot. The headline interaction
   effect.

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
